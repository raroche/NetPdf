// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.OpenType.Cff;

/// <summary>
/// Parsed CFF charset (Adobe Technical Note #5176 §"13 Charsets"). Maps glyph index →
/// SID (for name-keyed fonts) or CID (for CID-keyed fonts). Glyph 0 is implicitly
/// <c>.notdef</c> (SID 0) — the on-disk format only carries entries for glyphs 1..N-1.
/// </summary>
/// <remarks>
/// Three formats:
/// <list type="bullet">
/// <item>Format 0: array of <c>nGlyphs - 1</c> uint16 entries.</item>
/// <item>Format 1: ranges of <c>(firstSID: uint16, nLeft: uint8)</c>.</item>
/// <item>Format 2: ranges of <c>(firstSID: uint16, nLeft: uint16)</c> — for very large fonts.</item>
/// </list>
/// All three are supported.
/// </remarks>
internal sealed class CffCharset
{
    /// <summary>Format byte (0, 1, or 2).</summary>
    public required byte Format { get; init; }

    /// <summary>
    /// Per-glyph SID/CID, indexed by glyph id. Length == numGlyphs. Index 0 is always 0
    /// (.notdef), per spec.
    /// </summary>
    public required ushort[] GlyphSidOrCid { get; init; }

    public ushort GetGlyphSidOrCid(int glyphIndex)
    {
        if ((uint)glyphIndex >= (uint)GlyphSidOrCid.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(glyphIndex), glyphIndex,
                $"Glyph index {glyphIndex} out of range (numGlyphs = {GlyphSidOrCid.Length}).");
        }
        return GlyphSidOrCid[glyphIndex];
    }

    public static CffCharset Parse(ReadOnlySpan<byte> charsetBytes, int numGlyphs)
    {
        if (numGlyphs < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(numGlyphs), "numGlyphs must be at least 1.");
        }
        if (charsetBytes.Length < 1)
        {
            throw new InvalidDataException("CFF charset: missing format byte.");
        }
        var format = charsetBytes[0];
        var entries = new ushort[numGlyphs];
        // Glyph 0 is implicitly .notdef (SID 0); never stored on disk.
        entries[0] = 0;

        switch (format)
        {
            case 0:
                ParseFormat0(charsetBytes, numGlyphs, entries);
                break;
            case 1:
                ParseRanges(charsetBytes, numGlyphs, entries, nLeftSize: 1);
                break;
            case 2:
                ParseRanges(charsetBytes, numGlyphs, entries, nLeftSize: 2);
                break;
            default:
                throw new InvalidDataException($"CFF charset: unsupported format {format} (spec defines 0, 1, 2).");
        }

        return new CffCharset
        {
            Format = format,
            GlyphSidOrCid = entries,
        };
    }

    private static void ParseFormat0(ReadOnlySpan<byte> bytes, int numGlyphs, ushort[] entries)
    {
        var expectedLength = 1 + ((numGlyphs - 1) * 2);
        if (bytes.Length < expectedLength)
        {
            throw new InvalidDataException(
                $"CFF charset format 0: expected {expectedLength} bytes for {numGlyphs - 1} entr(ies); got {bytes.Length}.");
        }
        for (var i = 1; i < numGlyphs; i++)
        {
            var byteIndex = 1 + ((i - 1) * 2);
            entries[i] = (ushort)((bytes[byteIndex] << 8) | bytes[byteIndex + 1]);
        }
    }

    private static void ParseRanges(ReadOnlySpan<byte> bytes, int numGlyphs, ushort[] entries, int nLeftSize)
    {
        var entryStride = 2 + nLeftSize; // first(2) + nLeft(1 or 2)
        var pos = 1;
        var glyph = 1;
        while (glyph < numGlyphs)
        {
            if (bytes.Length < pos + entryStride)
            {
                throw new InvalidDataException(
                    $"CFF charset format {(nLeftSize == 1 ? 1 : 2)}: range table truncated " +
                    $"(needed {entryStride} more byte(s) at offset {pos}; have {bytes.Length - pos}).");
            }
            var first = (ushort)((bytes[pos] << 8) | bytes[pos + 1]);
            int nLeft = nLeftSize == 1
                ? bytes[pos + 2]
                : (bytes[pos + 2] << 8) | bytes[pos + 3];
            pos += entryStride;

            var rangeLength = nLeft + 1;
            if (glyph + rangeLength > numGlyphs)
            {
                throw new InvalidDataException(
                    $"CFF charset format {(nLeftSize == 1 ? 1 : 2)}: range starting at glyph {glyph} " +
                    $"with length {rangeLength} overruns numGlyphs ({numGlyphs}).");
            }
            // Detect ranges that would silently wrap a SID/CID past 0xFFFF — without this guard
            // the (ushort) cast inside the assignment loop would re-emit them as low values,
            // corrupting the structural identity downstream consumers rely on.
            if (first + (long)rangeLength - 1 > 0xFFFF)
            {
                throw new InvalidDataException(
                    $"CFF charset format {(nLeftSize == 1 ? 1 : 2)}: range starting at SID/CID {first} " +
                    $"with length {rangeLength} would wrap past 0xFFFF — last value would be {first + rangeLength - 1}.");
            }
            for (var k = 0; k < rangeLength; k++)
            {
                entries[glyph + k] = (ushort)(first + k);
            }
            glyph += rangeLength;
        }
    }
}
