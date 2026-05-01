// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.Text;

namespace NetPdf.Text.Fonts.OpenType;

/// <summary>
/// 4-byte ASCII table tags as <see cref="uint"/> values, big-endian-encoded. Storing tags
/// as <see cref="uint"/> instead of strings lets the table-directory hash table use
/// integer hashing and keeps the parse path allocation-free.
/// </summary>
/// <remarks>
/// Source: ISO/IEC 14496-22 (OpenType) §"Table directory" + Microsoft OpenType spec table
/// list (<c>https://learn.microsoft.com/typography/opentype/spec/otff</c>).
/// </remarks>
internal static class OpenTypeTags
{
    /// <summary>Font header — units per em, font bbox, indexToLocFormat. Required.</summary>
    public const uint Head = 0x68656164u; // "head"

    /// <summary>Horizontal header — ascender, descender, lineGap, numberOfHMetrics. Required.</summary>
    public const uint Hhea = 0x68686561u; // "hhea"

    /// <summary>Horizontal metrics — per-glyph advance width and lsb. Required.</summary>
    public const uint Hmtx = 0x686D7478u; // "hmtx"

    /// <summary>Maximum profile — numGlyphs and (TTF) various maxima. Required.</summary>
    public const uint Maxp = 0x6D617870u; // "maxp"

    /// <summary>Naming table — font family / subfamily / postscript name. Required.</summary>
    public const uint Name = 0x6E616D65u; // "name"

    /// <summary>OS/2 and Windows metrics — weight, width, typo metrics, x/cap height. Required.</summary>
    public const uint Os2 = 0x4F532F32u; // "OS/2"

    /// <summary>PostScript info — italic angle, underline, isFixedPitch. Required.</summary>
    public const uint Post = 0x706F7374u; // "post"

    /// <summary>Character-to-glyph mapping. Required.</summary>
    public const uint Cmap = 0x636D6170u; // "cmap"

    /// <summary>Glyph data offsets (TTF outlines). Required for TTF.</summary>
    public const uint Loca = 0x6C6F6361u; // "loca"

    /// <summary>Glyph outline data (TTF). Required for TTF.</summary>
    public const uint Glyf = 0x676C7966u; // "glyf"

    /// <summary>CFF / CFF2 outlines. Required for OTF/CFF.</summary>
    public const uint Cff = 0x43464620u;  // "CFF "

    /// <summary>SFNT magic for TTF (TrueType outlines). <c>0x00010000</c>.</summary>
    public const uint SfntVersionTtf = 0x00010000u;

    /// <summary>SFNT magic for OTF/CFF (PostScript outlines). ASCII <c>"OTTO"</c>.</summary>
    public const uint SfntVersionOtf = 0x4F54544Fu;

    /// <summary>SFNT magic for Apple TrueType (legacy). ASCII <c>"true"</c>.</summary>
    public const uint SfntVersionAppleTrue = 0x74727565u;

    /// <summary>Read a 4-byte ASCII tag from <paramref name="bytes"/> as a big-endian <see cref="uint"/>.</summary>
    public static uint FromAscii(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 4)
        {
            throw new ArgumentException("Tag must be exactly 4 bytes.", nameof(bytes));
        }
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    /// <summary>Format a tag as its 4-character ASCII representation. Useful for diagnostics.</summary>
    public static string ToAsciiString(uint tag)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, tag);
        return Encoding.ASCII.GetString(bytes);
    }
}
