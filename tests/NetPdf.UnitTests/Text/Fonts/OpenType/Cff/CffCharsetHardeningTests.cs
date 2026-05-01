// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.OpenType.Cff;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType.Cff;

/// <summary>
/// Post-Task-7 hardening: <see cref="CffCharset"/> formats 1 and 2 reject ranges that
/// would silently wrap a SID/CID past <c>0xFFFF</c>.
/// </summary>
public sealed class CffCharsetHardeningTests
{
    [Fact]
    public void Format1_rejects_range_that_wraps_past_uint16_max()
    {
        // Format 1 entry: first=0xFFFE, nLeft=5 → range covers [0xFFFE, 0x10003] → wraps.
        // numGlyphs is sized so the over-numGlyphs guard fires AFTER the wrap guard
        // (we pick numGlyphs=7 so the range fits length-wise but overflows value-wise).
        var bytes = new byte[]
        {
            1,                  // format
            0xFF, 0xFE, 5,      // first=0xFFFE, nLeft=5 → 6-glyph range
        };
        var ex = Assert.Throws<InvalidDataException>(() => CffCharset.Parse(bytes, numGlyphs: 7));
        Assert.Contains("0xFFFF", ex.Message);
    }

    [Fact]
    public void Format2_rejects_range_that_wraps_past_uint16_max()
    {
        var bytes = new byte[]
        {
            2,                          // format
            0xFF, 0xFE, 0x00, 0x05,     // first=0xFFFE, nLeft=5
        };
        var ex = Assert.Throws<InvalidDataException>(() => CffCharset.Parse(bytes, numGlyphs: 7));
        Assert.Contains("0xFFFF", ex.Message);
    }

    [Fact]
    public void Format1_accepts_range_ending_exactly_at_uint16_max()
    {
        // first=0xFFFD, nLeft=2 → covers 0xFFFD, 0xFFFE, 0xFFFF — last value is exactly 0xFFFF.
        // That's still valid; only > 0xFFFF should trip the guard.
        var bytes = new byte[]
        {
            1,
            0xFF, 0xFD, 2,
        };
        var charset = CffCharset.Parse(bytes, numGlyphs: 4);
        Assert.Equal((ushort)0xFFFD, charset.GetGlyphSidOrCid(1));
        Assert.Equal((ushort)0xFFFE, charset.GetGlyphSidOrCid(2));
        Assert.Equal((ushort)0xFFFF, charset.GetGlyphSidOrCid(3));
    }
}
