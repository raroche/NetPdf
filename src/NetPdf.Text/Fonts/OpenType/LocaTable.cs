// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.OpenType;

/// <summary>
/// Parsed <c>loca</c> table (OpenType §"loca"). Holds the byte offsets of each glyph
/// inside <c>glyf</c>, plus a sentinel offset at index <c>numGlyphs</c> so the length
/// of glyph <c>i</c> is <c>Offsets[i+1] - Offsets[i]</c>.
/// </summary>
/// <remarks>
/// Wire format depends on <see cref="HeadTable.IndexToLocFormat"/>: 0 = short (uint16
/// offsets, divided by 2) and 1 = long (uint32 offsets). Phase 1 normalizes both to
/// uint32 in <see cref="Offsets"/> for uniform downstream access.
/// </remarks>
internal sealed class LocaTable
{
    /// <summary>
    /// Offsets into <c>glyf</c>; length == <see cref="MaxpTable.NumGlyphs"/> + 1. Always
    /// non-decreasing per spec; equal consecutive entries denote zero-length glyphs.
    /// </summary>
    public required uint[] Offsets { get; init; }

    public int NumGlyphs => Offsets.Length - 1;

    /// <summary>Returns the byte length of glyph <paramref name="glyphIndex"/> inside <c>glyf</c>.</summary>
    public uint GetGlyphLength(int glyphIndex)
    {
        if ((uint)glyphIndex >= (uint)NumGlyphs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(glyphIndex), glyphIndex,
                $"Glyph index {glyphIndex} out of range (numGlyphs = {NumGlyphs}).");
        }
        return Offsets[glyphIndex + 1] - Offsets[glyphIndex];
    }

    public static LocaTable Parse(ReadOnlySpan<byte> tableBytes, ushort numGlyphs, short indexToLocFormat)
    {
        if (numGlyphs == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numGlyphs), "numGlyphs must be at least 1.");
        }
        var entryCount = numGlyphs + 1;
        var offsets = new uint[entryCount];

        if (indexToLocFormat == 0)
        {
            var expected = entryCount * 2;
            if (tableBytes.Length < expected)
            {
                throw new InvalidDataException(
                    $"loca (short): expected {expected} bytes for {entryCount} entr(ies); got {tableBytes.Length}.");
            }
            var reader = new BigEndianReader(tableBytes);
            for (var i = 0; i < entryCount; i++)
            {
                // Stored as uint16 of (offset / 2); restore by multiplying.
                offsets[i] = (uint)reader.ReadUInt16() * 2u;
            }
        }
        else if (indexToLocFormat == 1)
        {
            var expected = entryCount * 4;
            if (tableBytes.Length < expected)
            {
                throw new InvalidDataException(
                    $"loca (long): expected {expected} bytes for {entryCount} entr(ies); got {tableBytes.Length}.");
            }
            var reader = new BigEndianReader(tableBytes);
            for (var i = 0; i < entryCount; i++)
            {
                offsets[i] = reader.ReadUInt32();
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(
                nameof(indexToLocFormat), indexToLocFormat,
                "indexToLocFormat must be 0 (short) or 1 (long).");
        }

        for (var i = 1; i < entryCount; i++)
        {
            if (offsets[i] < offsets[i - 1])
            {
                throw new InvalidDataException(
                    $"loca: offsets must be non-decreasing; offset[{i}] ({offsets[i]}) < offset[{i - 1}] ({offsets[i - 1]}).");
            }
        }

        return new LocaTable { Offsets = offsets };
    }
}
