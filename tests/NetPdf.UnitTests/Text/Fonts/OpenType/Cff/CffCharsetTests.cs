// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.OpenType.Cff;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType.Cff;

public sealed class CffCharsetTests
{
    [Fact]
    public void Format0_decodes_per_glyph_sids_with_implicit_notdef_at_zero()
    {
        // numGlyphs=3 → 1 byte format + 2 entries × 2 bytes.
        // Glyph 1 → SID 391, glyph 2 → SID 392.
        var bytes = new byte[] { 0, 0x01, 0x87, 0x01, 0x88 };
        var charset = CffCharset.Parse(bytes, numGlyphs: 3);
        Assert.Equal((byte)0, charset.Format);
        Assert.Equal((ushort)0, charset.GetGlyphSidOrCid(0)); // .notdef implicit
        Assert.Equal((ushort)391, charset.GetGlyphSidOrCid(1));
        Assert.Equal((ushort)392, charset.GetGlyphSidOrCid(2));
    }

    [Fact]
    public void Format1_decodes_uint8_ranges()
    {
        // Format 1: 1-byte format + range entries (firstSID:uint16, nLeft:uint8).
        // numGlyphs=5 → 4 entries to fill (glyph 1..4).
        // Range A: first=10, nLeft=2 → glyphs 1,2,3 → SIDs 10,11,12.
        // Range B: first=20, nLeft=0 → glyph 4 → SID 20.
        var bytes = new byte[]
        {
            1,                  // format
            0, 10, 2,           // range A: first=10, nLeft=2
            0, 20, 0,           // range B: first=20, nLeft=0
        };
        var charset = CffCharset.Parse(bytes, numGlyphs: 5);
        Assert.Equal((ushort)10, charset.GetGlyphSidOrCid(1));
        Assert.Equal((ushort)11, charset.GetGlyphSidOrCid(2));
        Assert.Equal((ushort)12, charset.GetGlyphSidOrCid(3));
        Assert.Equal((ushort)20, charset.GetGlyphSidOrCid(4));
    }

    [Fact]
    public void Format2_decodes_uint16_ranges()
    {
        // Format 2: like format 1 but nLeft is uint16. numGlyphs=4 → 3 entries.
        // Range A: first=100, nLeft=2 → glyphs 1,2,3 → SIDs 100,101,102.
        var bytes = new byte[]
        {
            2,                  // format
            0, 100, 0, 2,       // range: first=100, nLeft=2
        };
        var charset = CffCharset.Parse(bytes, numGlyphs: 4);
        Assert.Equal((ushort)100, charset.GetGlyphSidOrCid(1));
        Assert.Equal((ushort)101, charset.GetGlyphSidOrCid(2));
        Assert.Equal((ushort)102, charset.GetGlyphSidOrCid(3));
    }

    [Fact]
    public void Parse_throws_on_unsupported_format()
    {
        var bytes = new byte[] { 7, 0, 1 };
        Assert.Throws<InvalidDataException>(() => CffCharset.Parse(bytes, numGlyphs: 2));
    }

    [Fact]
    public void Format1_throws_when_range_overruns_numGlyphs()
    {
        // Format 1 with a range that claims more glyphs than declared numGlyphs.
        var bytes = new byte[] { 1, 0, 10, 5 }; // first=10, nLeft=5 → 6 glyphs but only 1 left to fill
        Assert.Throws<InvalidDataException>(() => CffCharset.Parse(bytes, numGlyphs: 2));
    }

    [Fact]
    public void Format0_throws_when_buffer_too_small()
    {
        var bytes = new byte[] { 0, 0x01 }; // claims numGlyphs=3 but only 1 byte after format
        Assert.Throws<InvalidDataException>(() => CffCharset.Parse(bytes, numGlyphs: 3));
    }

    [Fact]
    public void GetGlyphSidOrCid_throws_for_out_of_range_index()
    {
        var bytes = new byte[] { 0, 0x00, 0x01 };
        var charset = CffCharset.Parse(bytes, numGlyphs: 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => charset.GetGlyphSidOrCid(5));
    }
}
