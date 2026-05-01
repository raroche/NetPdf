// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType;

public sealed class GlyfTableTests
{
    [Fact]
    public void GetGlyphBytes_returns_empty_for_zero_length_glyph()
    {
        var loca = LocaTable.Parse(SyntheticFont.LocaBytes(), SyntheticFont.NumGlyphs, indexToLocFormat: 1);
        var glyf = new GlyfTable(loca) { RawBytes = SyntheticFont.GlyfBytes() };
        var bytes = glyf.GetGlyphBytes(0); // glyph 0 = .notdef, empty
        Assert.True(bytes.IsEmpty);
        Assert.True(glyf.IsEmptyGlyph(0));
    }

    [Fact]
    public void GetGlyphBytes_returns_full_glyph_record_for_glyph_1()
    {
        var loca = LocaTable.Parse(SyntheticFont.LocaBytes(), SyntheticFont.NumGlyphs, indexToLocFormat: 1);
        var glyf = new GlyfTable(loca) { RawBytes = SyntheticFont.GlyfBytes() };
        var bytes = glyf.GetGlyphBytes(1);
        Assert.Equal(36, bytes.Length);
        // numberOfContours sits at offset 0..2 of the glyph header (big-endian)
        Assert.Equal((byte)0, bytes[0]);
        Assert.Equal((byte)1, bytes[1]);
    }

    [Fact]
    public void GetGlyphBytes_throws_for_out_of_range_glyph()
    {
        var loca = LocaTable.Parse(SyntheticFont.LocaBytes(), SyntheticFont.NumGlyphs, indexToLocFormat: 1);
        var glyf = new GlyfTable(loca) { RawBytes = SyntheticFont.GlyfBytes() };
        Assert.Throws<ArgumentOutOfRangeException>(() => glyf.GetGlyphBytes(SyntheticFont.NumGlyphs));
        Assert.Throws<ArgumentOutOfRangeException>(() => glyf.GetGlyphBytes(-1));
    }

    [Fact]
    public void GetGlyphBytes_throws_when_loca_offset_overshoots_glyf_bounds()
    {
        // Build a loca that points past the glyf payload (corruption).
        var locaBytes = new byte[4 * 4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(locaBytes.AsSpan(0, 4), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(locaBytes.AsSpan(4, 4), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(locaBytes.AsSpan(8, 4), 1000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(locaBytes.AsSpan(12, 4), 1000);

        var loca = LocaTable.Parse(locaBytes, numGlyphs: 3, indexToLocFormat: 1);
        var glyf = new GlyfTable(loca) { RawBytes = new byte[36] };
        Assert.Throws<InvalidDataException>(() => glyf.GetGlyphBytes(1));
    }
}
