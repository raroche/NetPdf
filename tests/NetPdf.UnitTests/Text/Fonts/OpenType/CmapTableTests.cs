// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType;

public sealed class CmapTableTests
{
    [Fact]
    public void Parse_synthetic_font_picks_format4_subtable()
    {
        var cmap = CmapTable.Parse(SyntheticFont.CmapBytes());
        Assert.Equal((ushort)3, cmap.SelectedPlatformId);
        Assert.Equal((ushort)1, cmap.SelectedEncodingId);
        Assert.Equal((ushort)4, cmap.SelectedFormat);
    }

    [Fact]
    public void GetGlyphId_resolves_letter_A_and_B()
    {
        var cmap = CmapTable.Parse(SyntheticFont.CmapBytes());
        Assert.Equal((ushort)1, cmap.GetGlyphId('A'));
        Assert.Equal((ushort)2, cmap.GetGlyphId('B'));
    }

    [Fact]
    public void GetGlyphId_returns_zero_for_unmapped_codepoint()
    {
        var cmap = CmapTable.Parse(SyntheticFont.CmapBytes());
        Assert.Equal((ushort)0, cmap.GetGlyphId('Z'));
        Assert.Equal((ushort)0, cmap.GetGlyphId(0x1F600));
    }

    [Fact]
    public void Parse_format12_subtable_resolves_supplementary_plane_codepoints()
    {
        // Build a cmap with one format-12 subtable containing one group [0x1F600, 0x1F602] → glyphs 5, 6, 7.
        var subtable = BuildFormat12Single(start: 0x1F600u, end: 0x1F602u, startGlyph: 5);
        var bytes = new byte[4 + 8 + subtable.Length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 0);          // version
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], 1);          // numTables
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], 3);          // platformID = Windows
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 10);         // encodingID = UCS-4 (best score)
        BinaryPrimitives.WriteUInt32BigEndian(span[8..12], 12);        // offset
        subtable.CopyTo(span[12..]);

        var cmap = CmapTable.Parse(bytes);
        Assert.Equal((ushort)12, cmap.SelectedFormat);
        Assert.Equal((ushort)5, cmap.GetGlyphId(0x1F600));
        Assert.Equal((ushort)6, cmap.GetGlyphId(0x1F601));
        Assert.Equal((ushort)7, cmap.GetGlyphId(0x1F602));
    }

    [Fact]
    public void Parse_throws_when_no_subtable_format_supported()
    {
        // numTables=1 with platformID=2 / encodingID=0 (ISO platform — no score)
        var bytes = new byte[4 + 8];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), 2);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 12);
        Assert.Throws<InvalidDataException>(() => CmapTable.Parse(bytes));
    }

    [Fact]
    public void Parse_throws_when_subtable_offset_out_of_range()
    {
        var bytes = SyntheticFont.CmapBytes();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 9999);
        Assert.Throws<InvalidDataException>(() => CmapTable.Parse(bytes));
    }

    private static byte[] BuildFormat12Single(uint start, uint end, ushort startGlyph)
    {
        var bytes = new byte[16 + 12];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 12);            // format
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], 0);             // reserved
        BinaryPrimitives.WriteUInt32BigEndian(span[4..8], (uint)bytes.Length); // length
        BinaryPrimitives.WriteUInt32BigEndian(span[8..12], 0);            // language
        BinaryPrimitives.WriteUInt32BigEndian(span[12..16], 1);           // numGroups
        BinaryPrimitives.WriteUInt32BigEndian(span[16..20], start);
        BinaryPrimitives.WriteUInt32BigEndian(span[20..24], end);
        BinaryPrimitives.WriteUInt32BigEndian(span[24..28], startGlyph);
        return bytes;
    }
}
