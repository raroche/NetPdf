// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.Text;

namespace NetPdf.UnitTests.Text.Fonts.OpenType;

/// <summary>
/// Builds a minimal-but-valid TTF byte stream for parser tests. Tests that need a "real"
/// font feed the bytes from <see cref="Build"/> through <c>OpenTypeFont.Parse</c>; tests
/// that exercise individual parsers can grab single tables via <see cref="HeadBytes"/> /
/// <see cref="CmapBytes"/> etc.
/// </summary>
/// <remarks>
/// The synthetic font has 3 glyphs: glyph 0 (.notdef, empty), glyph 1 (mapped from 'A' /
/// U+0041, simple square 0..500), glyph 2 (mapped from 'B' / U+0042, empty). The cmap is
/// format 4. Tables are emitted in the canonical sorted-tag order.
/// </remarks>
internal static class SyntheticFont
{
    public const ushort UnitsPerEm = 1000;
    public const ushort NumGlyphs = 3;
    public const ushort NumberOfHMetrics = 3;

    /// <summary>Builds the full SFNT byte stream — header + table directory + 10 tables.</summary>
    public static byte[] Build()
    {
        var head = HeadBytes();
        var hhea = HheaBytes();
        var maxp = MaxpBytes();
        var os2 = Os2Bytes();
        var post = PostBytes();
        var name = NameBytes();
        var hmtx = HmtxBytes();
        var cmap = CmapBytes();
        var loca = LocaBytes();
        var glyf = GlyfBytes();

        // Tag → bytes, sorted by tag for the on-disk directory order (OpenType spec).
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

        const int sfntHeaderSize = 12;
        const int recordSize = 16;
        var firstTableOffset = sfntHeaderSize + (recordSize * tables.Length);

        // Compute table offsets (4-byte aligned).
        var offsets = new int[tables.Length];
        var cursor = firstTableOffset;
        for (var i = 0; i < tables.Length; i++)
        {
            offsets[i] = cursor;
            cursor += AlignTo4(tables[i].Bytes.Length);
        }
        var totalSize = cursor;

        var output = new byte[totalSize];
        var writer = output.AsSpan();

        // SFNT header.
        BinaryPrimitives.WriteUInt32BigEndian(writer[..4], 0x00010000u); // sfntVersion = TTF
        BinaryPrimitives.WriteUInt16BigEndian(writer[4..6], (ushort)tables.Length);
        // searchRange / entrySelector / rangeShift — values per spec for 10 tables, but parsers don't rely on them
        BinaryPrimitives.WriteUInt16BigEndian(writer[6..8], 128);   // searchRange = 16 * (max pow2 ≤ 10) = 16*8 = 128
        BinaryPrimitives.WriteUInt16BigEndian(writer[8..10], 3);    // entrySelector = log2(8) = 3
        BinaryPrimitives.WriteUInt16BigEndian(writer[10..12], (ushort)((tables.Length * 16) - 128));

        // Table directory.
        var directoryCursor = sfntHeaderSize;
        for (var i = 0; i < tables.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(writer[directoryCursor..(directoryCursor + 4)], tables[i].Tag);
            BinaryPrimitives.WriteUInt32BigEndian(writer[(directoryCursor + 4)..(directoryCursor + 8)], 0); // checksum unused
            BinaryPrimitives.WriteUInt32BigEndian(writer[(directoryCursor + 8)..(directoryCursor + 12)], (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(writer[(directoryCursor + 12)..(directoryCursor + 16)], (uint)tables[i].Bytes.Length);
            directoryCursor += recordSize;
        }

        // Tables themselves.
        for (var i = 0; i < tables.Length; i++)
        {
            tables[i].Bytes.CopyTo(writer[offsets[i]..]);
        }

        return output;
    }

    public static byte[] HeadBytes()
    {
        var bytes = new byte[54];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 1);           // majorVersion
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], 0);           // minorVersion
        BinaryPrimitives.WriteUInt32BigEndian(span[4..8], 0x00010000u); // fontRevision
        BinaryPrimitives.WriteUInt32BigEndian(span[8..12], 0);          // checkSumAdjustment
        BinaryPrimitives.WriteUInt32BigEndian(span[12..16], 0x5F0F3CF5u); // magicNumber
        BinaryPrimitives.WriteUInt16BigEndian(span[16..18], 0);         // flags
        BinaryPrimitives.WriteUInt16BigEndian(span[18..20], UnitsPerEm);
        BinaryPrimitives.WriteInt64BigEndian(span[20..28], 0);          // created
        BinaryPrimitives.WriteInt64BigEndian(span[28..36], 0);          // modified
        BinaryPrimitives.WriteInt16BigEndian(span[36..38], 0);          // xMin
        BinaryPrimitives.WriteInt16BigEndian(span[38..40], 0);          // yMin
        BinaryPrimitives.WriteInt16BigEndian(span[40..42], 500);        // xMax
        BinaryPrimitives.WriteInt16BigEndian(span[42..44], 700);        // yMax
        BinaryPrimitives.WriteUInt16BigEndian(span[44..46], 0);         // macStyle
        BinaryPrimitives.WriteUInt16BigEndian(span[46..48], 8);         // lowestRecPPEM
        BinaryPrimitives.WriteInt16BigEndian(span[48..50], 0);          // fontDirectionHint
        BinaryPrimitives.WriteInt16BigEndian(span[50..52], 1);          // indexToLocFormat = long
        BinaryPrimitives.WriteInt16BigEndian(span[52..54], 0);          // glyphDataFormat
        return bytes;
    }

    public static byte[] HheaBytes()
    {
        var bytes = new byte[36];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 1);           // majorVersion
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], 0);           // minorVersion
        BinaryPrimitives.WriteInt16BigEndian(span[4..6], 800);          // ascender
        BinaryPrimitives.WriteInt16BigEndian(span[6..8], -200);         // descender
        BinaryPrimitives.WriteInt16BigEndian(span[8..10], 100);         // lineGap
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], 600);       // advanceWidthMax
        BinaryPrimitives.WriteInt16BigEndian(span[12..14], 0);          // minLsb
        BinaryPrimitives.WriteInt16BigEndian(span[14..16], 0);          // minRsb
        BinaryPrimitives.WriteInt16BigEndian(span[16..18], 600);        // xMaxExtent
        BinaryPrimitives.WriteInt16BigEndian(span[18..20], 1);          // caretSlopeRise
        BinaryPrimitives.WriteInt16BigEndian(span[20..22], 0);          // caretSlopeRun
        BinaryPrimitives.WriteInt16BigEndian(span[22..24], 0);          // caretOffset
        // 4 reserved int16 (zeros at span[24..32])
        BinaryPrimitives.WriteInt16BigEndian(span[32..34], 0);          // metricDataFormat
        BinaryPrimitives.WriteUInt16BigEndian(span[34..36], NumberOfHMetrics);
        return bytes;
    }

    public static byte[] MaxpBytes()
    {
        var bytes = new byte[32];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span[0..4], 0x00010000u); // version 1.0
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], NumGlyphs);
        // remaining v1.0 fields zero-initialized — fine for parser
        return bytes;
    }

    public static byte[] Os2Bytes()
    {
        var bytes = new byte[96];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 4);           // version 4
        BinaryPrimitives.WriteInt16BigEndian(span[2..4], 500);          // xAvgCharWidth
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], 400);         // weight = Regular
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 5);           // width = Medium
        BinaryPrimitives.WriteUInt16BigEndian(span[8..10], 0);          // fsType
        // PANOSE bytes at [32..42] left zero
        // sxHeight at [86..88] = 500; sCapHeight at [88..90] = 700
        BinaryPrimitives.WriteInt16BigEndian(span[68..70], 800);        // sTypoAscender
        BinaryPrimitives.WriteInt16BigEndian(span[70..72], -200);       // sTypoDescender
        BinaryPrimitives.WriteInt16BigEndian(span[72..74], 100);        // sTypoLineGap
        BinaryPrimitives.WriteUInt16BigEndian(span[74..76], 800);       // usWinAscent
        BinaryPrimitives.WriteUInt16BigEndian(span[76..78], 200);       // usWinDescent
        BinaryPrimitives.WriteUInt32BigEndian(span[78..82], 0);         // ulCodePageRange1
        BinaryPrimitives.WriteUInt32BigEndian(span[82..86], 0);         // ulCodePageRange2
        BinaryPrimitives.WriteInt16BigEndian(span[86..88], 500);        // sxHeight
        BinaryPrimitives.WriteInt16BigEndian(span[88..90], 700);        // sCapHeight
        BinaryPrimitives.WriteUInt16BigEndian(span[90..92], 0);         // usDefaultChar
        BinaryPrimitives.WriteUInt16BigEndian(span[92..94], 0x20);      // usBreakChar = space
        BinaryPrimitives.WriteUInt16BigEndian(span[94..96], 1);         // usMaxContext
        return bytes;
    }

    public static byte[] PostBytes()
    {
        var bytes = new byte[32];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span[0..4], 0x00030000u); // version 3.0
        BinaryPrimitives.WriteUInt32BigEndian(span[4..8], 0);           // italicAngle = 0
        BinaryPrimitives.WriteInt16BigEndian(span[8..10], -100);        // underlinePosition
        BinaryPrimitives.WriteInt16BigEndian(span[10..12], 50);         // underlineThickness
        BinaryPrimitives.WriteUInt32BigEndian(span[12..16], 0);         // isFixedPitch = false
        return bytes;
    }

    public static byte[] NameBytes()
    {
        // One record: PostScript name (nameID=6) on platform=Windows, encoding=Unicode-BMP, language=en-US.
        const string postScript = "Synth-Test";
        var encoded = Encoding.BigEndianUnicode.GetBytes(postScript);
        var headerSize = 6 + 12; // 6-byte header + 1 record × 12 bytes
        var bytes = new byte[headerSize + encoded.Length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 0);                      // format
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], 1);                      // count
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], (ushort)headerSize);     // storageOffset
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 3);                      // platformID = Windows
        BinaryPrimitives.WriteUInt16BigEndian(span[8..10], 1);                     // encodingID = Unicode-BMP
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], 0x0409);               // languageID = en-US
        BinaryPrimitives.WriteUInt16BigEndian(span[12..14], 6);                    // nameID = PostScript
        BinaryPrimitives.WriteUInt16BigEndian(span[14..16], (ushort)encoded.Length); // length
        BinaryPrimitives.WriteUInt16BigEndian(span[16..18], 0);                    // string offset
        encoded.CopyTo(span[headerSize..]);
        return bytes;
    }

    public static byte[] HmtxBytes()
    {
        // 3 glyphs, all longHorMetric: { advance, lsb }
        // glyph 0 (.notdef): advance 600, lsb 0
        // glyph 1 ('A'):     advance 500, lsb 0
        // glyph 2 ('B'):     advance 500, lsb 0
        var bytes = new byte[NumberOfHMetrics * 4];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 600);
        BinaryPrimitives.WriteInt16BigEndian(span[2..4], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], 500);
        BinaryPrimitives.WriteInt16BigEndian(span[6..8], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..10], 500);
        BinaryPrimitives.WriteInt16BigEndian(span[10..12], 0);
        return bytes;
    }

    public static byte[] CmapBytes()
    {
        // Format 4 with two segments:
        //   segment 1: [0x0041, 0x0042] → glyphs 1, 2 (idDelta = -0x40, idRangeOffset = 0)
        //   segment 2 (terminator): [0xFFFF, 0xFFFF] → glyph 0 (idDelta = 1)
        // Encoding records: one entry pointing to format-4 subtable.
        var subtable = BuildFormat4Subtable();
        var bytes = new byte[4 + 8 + subtable.Length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 0);         // version
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], 1);         // numTables
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], 3);         // platformID = Windows
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 1);         // encodingID = Unicode-BMP
        BinaryPrimitives.WriteUInt32BigEndian(span[8..12], 12);       // subtable offset
        subtable.CopyTo(span[12..]);
        return bytes;
    }

    private static byte[] BuildFormat4Subtable()
    {
        const int segCount = 2; // [A..B], [terminator]
        var bytes = new byte[14 + 2 + (segCount * 8)]; // header (14) + reservedPad (2) + 4 arrays × segCount × 2
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 4);         // format
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], (ushort)bytes.Length); // length
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], 0);         // language
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], (ushort)(segCount * 2)); // segCountX2
        BinaryPrimitives.WriteUInt16BigEndian(span[8..10], 4);        // searchRange (loosely)
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], 1);       // entrySelector
        BinaryPrimitives.WriteUInt16BigEndian(span[12..14], 0);       // rangeShift

        var endStart = 14;
        var startStart = endStart + (segCount * 2) + 2; // + reservedPad
        var deltaStart = startStart + (segCount * 2);
        var rangeOffsetStart = deltaStart + (segCount * 2);

        // segment 0
        BinaryPrimitives.WriteUInt16BigEndian(span[endStart..(endStart + 2)], 0x0042);
        BinaryPrimitives.WriteUInt16BigEndian(span[startStart..(startStart + 2)], 0x0041);
        BinaryPrimitives.WriteInt16BigEndian(span[deltaStart..(deltaStart + 2)], unchecked((short)(1 - 0x0041))); // glyph 1 for 'A'
        BinaryPrimitives.WriteUInt16BigEndian(span[rangeOffsetStart..(rangeOffsetStart + 2)], 0);

        // segment 1 (terminator)
        BinaryPrimitives.WriteUInt16BigEndian(span[(endStart + 2)..(endStart + 4)], 0xFFFF);
        BinaryPrimitives.WriteUInt16BigEndian(span[(startStart + 2)..(startStart + 4)], 0xFFFF);
        BinaryPrimitives.WriteInt16BigEndian(span[(deltaStart + 2)..(deltaStart + 4)], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[(rangeOffsetStart + 2)..(rangeOffsetStart + 4)], 0);

        // reservedPad already zero
        return bytes;
    }

    public static byte[] LocaBytes()
    {
        // long format (head.indexToLocFormat = 1), entryCount = numGlyphs + 1 = 4
        // glyph 0: 0..0 (empty)
        // glyph 1: 0..36 (simple glyph, 36 bytes — see GlyfBytes)
        // glyph 2: 36..36 (empty)
        // sentinel: 36
        var bytes = new byte[4 * 4];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span[0..4], 0);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..8], 0);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..12], 36);
        BinaryPrimitives.WriteUInt32BigEndian(span[12..16], 36);
        return bytes;
    }

    public static byte[] GlyfBytes()
    {
        // Glyph 1: a simple glyph with 1 contour (a triangle), 3 points.
        //   numberOfContours = 1, xMin = 0, yMin = 0, xMax = 500, yMax = 500
        //   endPtsOfContours[1] = {2}
        //   instructionLength = 0
        //   flags[3] = {0x37, 0x37, 0x37} = on-curve + xShortVector + yShortVector + repeat? — keep simple, no repeats
        //   xCoords[3], yCoords[3] (1 byte each since short-vector)
        //
        // Total: 10 (header) + 2 (endPts) + 2 (instructionLength) + 3 (flags) + 3 (xCoords) + 3 (yCoords)
        //      + 13 padding bytes to reach 36 (round up for clean offset arithmetic).
        // For Phase 1 the glyph contents aren't deeply parsed; we just need predictable bytes.
        var bytes = new byte[36];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteInt16BigEndian(span[0..2], 1);     // numberOfContours
        BinaryPrimitives.WriteInt16BigEndian(span[2..4], 0);     // xMin
        BinaryPrimitives.WriteInt16BigEndian(span[4..6], 0);     // yMin
        BinaryPrimitives.WriteInt16BigEndian(span[6..8], 500);   // xMax
        BinaryPrimitives.WriteInt16BigEndian(span[8..10], 500);  // yMax
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], 2);  // endPtsOfContours[0] = 2
        BinaryPrimitives.WriteUInt16BigEndian(span[12..14], 0);  // instructionLength = 0
        // remainder bytes (flags + coords + padding) left zero — never deeply parsed in Phase 1
        return bytes;
    }

    private static int AlignTo4(int length) => (length + 3) & ~3;
}
