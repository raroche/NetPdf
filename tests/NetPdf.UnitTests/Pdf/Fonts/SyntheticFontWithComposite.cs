// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;

namespace NetPdf.UnitTests.Pdf.Fonts;

/// <summary>
/// 4-glyph TTF byte stream variant of <see cref="NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont"/>
/// that adds a composite glyph at id 3 referencing glyph 1. Used by the subsetter tests
/// to exercise composite-glyph chase + component glyphIndex rewriting.
/// </summary>
/// <remarks>
/// Glyph layout in <c>glyf</c>:
/// <list type="bullet">
/// <item>Glyph 0 (.notdef): empty (loca offset 0..0).</item>
/// <item>Glyph 1: simple, 36 bytes (reused from <see cref="NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont"/>).</item>
/// <item>Glyph 2: empty (loca offset 36..36).</item>
/// <item>Glyph 3: composite, 18 bytes — references glyph 1 with no transform and no further components.</item>
/// </list>
/// </remarks>
internal static class SyntheticFontWithComposite
{
    public const ushort NumGlyphs = 4;
    public const int CompositeGlyphIndex = 3;
    public const int CompositeReferencedGlyph = 1;

    public static byte[] Build()
    {
        var head = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.HeadBytes();
        var hhea = HheaBytes();
        var maxp = MaxpBytes();
        var os2 = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.Os2Bytes();
        var post = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.PostBytes();
        var name = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.NameBytes();
        var cmap = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.CmapBytes();
        var hmtx = HmtxBytes();
        var loca = LocaBytes();
        var glyf = GlyfBytes();

        var tables = new (uint Tag, byte[] Bytes)[]
        {
            (0x4F532F32u, os2),  // OS/2
            (0x636D6170u, cmap), // cmap
            (0x676C7966u, glyf), // glyf
            (0x68656164u, head), // head
            (0x68686561u, hhea), // hhea
            (0x686D7478u, hmtx), // hmtx
            (0x6C6F6361u, loca), // loca
            (0x6D617870u, maxp), // maxp
            (0x6E616D65u, name), // name
            (0x706F7374u, post), // post
        };
        Array.Sort(tables, (a, b) => a.Tag.CompareTo(b.Tag));

        const int sfntHeaderSize = 12;
        const int recordSize = 16;
        var firstTableOffset = sfntHeaderSize + (recordSize * tables.Length);

        var offsets = new int[tables.Length];
        var cursor = firstTableOffset;
        for (var i = 0; i < tables.Length; i++)
        {
            offsets[i] = cursor;
            cursor += AlignTo4(tables[i].Bytes.Length);
        }
        var output = new byte[cursor];
        var span = output.AsSpan();

        BinaryPrimitives.WriteUInt32BigEndian(span[..4], 0x00010000u); // TTF
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], (ushort)tables.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 128);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..10], 3);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], (ushort)((tables.Length * 16) - 128));

        var directoryCursor = sfntHeaderSize;
        for (var i = 0; i < tables.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(span[directoryCursor..(directoryCursor + 4)], tables[i].Tag);
            BinaryPrimitives.WriteUInt32BigEndian(span[(directoryCursor + 4)..(directoryCursor + 8)], 0);
            BinaryPrimitives.WriteUInt32BigEndian(span[(directoryCursor + 8)..(directoryCursor + 12)], (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(span[(directoryCursor + 12)..(directoryCursor + 16)], (uint)tables[i].Bytes.Length);
            directoryCursor += recordSize;
        }
        for (var i = 0; i < tables.Length; i++)
        {
            tables[i].Bytes.CopyTo(span[offsets[i]..]);
        }
        return output;
    }

    private static byte[] HheaBytes()
    {
        var bytes = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.HheaBytes();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(34, 2), NumGlyphs);
        return bytes;
    }

    private static byte[] MaxpBytes()
    {
        var bytes = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.MaxpBytes();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), NumGlyphs);
        return bytes;
    }

    private static byte[] HmtxBytes()
    {
        // 4 longHorMetric records.
        var bytes = new byte[NumGlyphs * 4];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 600);
        BinaryPrimitives.WriteInt16BigEndian(span[2..4], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], 500);
        BinaryPrimitives.WriteInt16BigEndian(span[6..8], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..10], 500);
        BinaryPrimitives.WriteInt16BigEndian(span[10..12], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[12..14], 700);
        BinaryPrimitives.WriteInt16BigEndian(span[14..16], 0);
        return bytes;
    }

    private static byte[] LocaBytes()
    {
        // Long format: 5 uint32 offsets.
        // Glyph 0 = empty (0..0), glyph 1 = simple at 0..36, glyph 2 = empty (36..36),
        // glyph 3 = composite at 36..54.
        var bytes = new byte[5 * 4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 36);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12, 4), 36);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 54);
        return bytes;
    }

    private static byte[] GlyfBytes()
    {
        // [0..36)  = simple glyph 1 (reused from SyntheticFont)
        // [36..54) = composite glyph 3 referencing glyph 1
        var simple = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.GlyfBytes();
        var composite = BuildCompositeGlyph(referencedGlyphId: CompositeReferencedGlyph);
        var bytes = new byte[simple.Length + composite.Length];
        simple.CopyTo(bytes, 0);
        composite.CopyTo(bytes, simple.Length);
        return bytes;
    }

    /// <summary>
    /// Build an 18-byte composite glyph: 10-byte header + one 8-byte component record
    /// with no transform and no further components.
    /// </summary>
    public static byte[] BuildCompositeGlyph(int referencedGlyphId)
    {
        var bytes = new byte[18];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteInt16BigEndian(span[0..2], -1);   // numberOfContours = composite
        BinaryPrimitives.WriteInt16BigEndian(span[2..4], 0);    // xMin
        BinaryPrimitives.WriteInt16BigEndian(span[4..6], 0);    // yMin
        BinaryPrimitives.WriteInt16BigEndian(span[6..8], 500);  // xMax
        BinaryPrimitives.WriteInt16BigEndian(span[8..10], 500); // yMax

        // Component record. flags = 0x0001 (ARG_1_AND_2_ARE_WORDS) only — no MORE_COMPONENTS,
        // no transform, no instructions. This is a single-component composite.
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(span[12..14], (ushort)referencedGlyphId);
        BinaryPrimitives.WriteInt16BigEndian(span[14..16], 0); // arg1 (placement)
        BinaryPrimitives.WriteInt16BigEndian(span[16..18], 0); // arg2
        return bytes;
    }

    private static int AlignTo4(int length) => (length + 3) & ~3;
}
