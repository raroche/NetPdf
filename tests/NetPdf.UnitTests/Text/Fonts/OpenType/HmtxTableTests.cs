// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType;

public sealed class HmtxTableTests
{
    [Fact]
    public void Parse_synthetic_font_returns_one_entry_per_glyph()
    {
        var hmtx = HmtxTable.Parse(SyntheticFont.HmtxBytes(), SyntheticFont.NumberOfHMetrics, SyntheticFont.NumGlyphs);
        Assert.Equal(SyntheticFont.NumGlyphs, hmtx.AdvanceWidths.Length);
        Assert.Equal((ushort)600, hmtx.AdvanceWidths[0]);
        Assert.Equal((ushort)500, hmtx.AdvanceWidths[1]);
        Assert.Equal((ushort)500, hmtx.AdvanceWidths[2]);
    }

    [Fact]
    public void Trailing_lsb_only_entries_share_the_last_advance_width()
    {
        // 1 longHorMetric (advance=400, lsb=10) + 2 lsb-only int16 (lsb=20, lsb=30) for numGlyphs=3
        var bytes = new byte[(1 * 4) + (2 * 2)];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 400);
        BinaryPrimitives.WriteInt16BigEndian(span[2..4], 10);
        BinaryPrimitives.WriteInt16BigEndian(span[4..6], 20);
        BinaryPrimitives.WriteInt16BigEndian(span[6..8], 30);

        var hmtx = HmtxTable.Parse(bytes, numberOfHMetrics: 1, numGlyphs: 3);
        Assert.Equal(new ushort[] { 400, 400, 400 }, hmtx.AdvanceWidths);
        Assert.Equal(new short[] { 10, 20, 30 }, hmtx.LeftSideBearings);
    }

    [Fact]
    public void Parse_throws_when_numberOfHMetrics_exceeds_numGlyphs()
    {
        var bytes = new byte[16];
        Assert.Throws<ArgumentOutOfRangeException>(() => HmtxTable.Parse(bytes, numberOfHMetrics: 4, numGlyphs: 2));
    }

    [Fact]
    public void Parse_throws_when_numberOfHMetrics_is_zero()
    {
        var bytes = new byte[4];
        Assert.Throws<ArgumentOutOfRangeException>(() => HmtxTable.Parse(bytes, numberOfHMetrics: 0, numGlyphs: 2));
    }

    [Fact]
    public void Parse_throws_when_buffer_too_small()
    {
        var bytes = new byte[2];
        Assert.Throws<InvalidDataException>(() => HmtxTable.Parse(bytes, numberOfHMetrics: 1, numGlyphs: 1));
    }
}
