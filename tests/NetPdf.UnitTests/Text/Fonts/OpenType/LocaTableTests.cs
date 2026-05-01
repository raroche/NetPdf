// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType;

public sealed class LocaTableTests
{
    [Fact]
    public void Parse_long_format_decodes_offsets()
    {
        var loca = LocaTable.Parse(SyntheticFont.LocaBytes(), SyntheticFont.NumGlyphs, indexToLocFormat: 1);
        Assert.Equal(SyntheticFont.NumGlyphs, loca.NumGlyphs);
        Assert.Equal(0u, loca.Offsets[0]);
        Assert.Equal(0u, loca.Offsets[1]);
        Assert.Equal(36u, loca.Offsets[2]);
        Assert.Equal(36u, loca.Offsets[3]);
    }

    [Fact]
    public void Parse_short_format_doubles_stored_offsets()
    {
        // Short format stores uint16 of (offset / 2). Offsets: 0, 4, 16 → stored as 0, 2, 8.
        var bytes = new byte[3 * 2];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], 2);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], 8);

        var loca = LocaTable.Parse(bytes, numGlyphs: 2, indexToLocFormat: 0);
        Assert.Equal(0u, loca.Offsets[0]);
        Assert.Equal(4u, loca.Offsets[1]);
        Assert.Equal(16u, loca.Offsets[2]);
    }

    [Fact]
    public void GetGlyphLength_returns_difference_between_consecutive_offsets()
    {
        var loca = LocaTable.Parse(SyntheticFont.LocaBytes(), SyntheticFont.NumGlyphs, indexToLocFormat: 1);
        Assert.Equal(0u, loca.GetGlyphLength(0));
        Assert.Equal(36u, loca.GetGlyphLength(1));
        Assert.Equal(0u, loca.GetGlyphLength(2));
    }

    [Fact]
    public void GetGlyphLength_throws_for_out_of_range_glyph()
    {
        var loca = LocaTable.Parse(SyntheticFont.LocaBytes(), SyntheticFont.NumGlyphs, indexToLocFormat: 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => loca.GetGlyphLength(SyntheticFont.NumGlyphs));
        Assert.Throws<ArgumentOutOfRangeException>(() => loca.GetGlyphLength(-1));
    }

    [Fact]
    public void Parse_throws_on_decreasing_offsets()
    {
        // Three uint32 offsets: 10, 5, 20 — decreasing in the middle, must throw.
        var bytes = new byte[3 * 4];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span[0..4], 10);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..8], 5);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..12], 20);
        Assert.Throws<InvalidDataException>(() => LocaTable.Parse(bytes, numGlyphs: 2, indexToLocFormat: 1));
    }

    [Fact]
    public void Parse_throws_on_invalid_format_value()
    {
        var bytes = new byte[12];
        Assert.Throws<ArgumentOutOfRangeException>(() => LocaTable.Parse(bytes, numGlyphs: 2, indexToLocFormat: 7));
    }

    [Fact]
    public void Parse_throws_when_buffer_too_small()
    {
        var bytes = new byte[2];
        Assert.Throws<InvalidDataException>(() => LocaTable.Parse(bytes, numGlyphs: 2, indexToLocFormat: 1));
    }
}
