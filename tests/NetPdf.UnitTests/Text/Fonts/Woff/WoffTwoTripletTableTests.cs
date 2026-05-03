// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.Woff;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.Woff;

/// <summary>
/// Spot-check tests for the 128-entry triplet decoding table (W3C WOFF 2.0 §5.2 Table 6).
/// Each test pins one entry's (ByteCount, XBits, YBits, DeltaX, DeltaY, XSign, YSign)
/// tuple verbatim from the spec so a future regression in the table builder shows up
/// as a focused test failure rather than a downstream coordinate-computation error.
/// </summary>
public sealed class WoffTwoTripletTableTests
{
    [Fact]
    public void Has_exactly_128_entries()
    {
        Assert.Equal(128, WoffTwoTripletTable.Entries.Length);
    }

    [Theory]
    // Indices 0..9 — yBits=8, xBits=0; deltaY pairs by sign across (0, 256, 512, 768, 1024).
    [InlineData(0, 2, 0, 8, 0, 0, 0, -1)]
    [InlineData(1, 2, 0, 8, 0, 0, 0, +1)]
    [InlineData(2, 2, 0, 8, 0, 256, 0, -1)]
    [InlineData(3, 2, 0, 8, 0, 256, 0, +1)]
    [InlineData(8, 2, 0, 8, 0, 1024, 0, -1)]
    [InlineData(9, 2, 0, 8, 0, 1024, 0, +1)]
    // Indices 10..19 — xBits=8, yBits=0.
    [InlineData(10, 2, 8, 0, 0, 0, -1, 0)]
    [InlineData(11, 2, 8, 0, 0, 0, +1, 0)]
    [InlineData(18, 2, 8, 0, 1024, 0, -1, 0)]
    [InlineData(19, 2, 8, 0, 1024, 0, +1, 0)]
    // 4+4 region — pin endpoints + key transitions.
    [InlineData(20, 2, 4, 4, 1, 1, -1, -1)]
    [InlineData(23, 2, 4, 4, 1, 1, +1, +1)]
    [InlineData(35, 2, 4, 4, 49, 1, +1, +1)]
    [InlineData(83, 2, 4, 4, 17, 33, +1, +1)]
    // 8+8 region.
    [InlineData(84, 3, 8, 8, 1, 1, -1, -1)]
    [InlineData(87, 3, 8, 8, 1, 1, +1, +1)]
    [InlineData(116, 3, 8, 8, 513, 513, -1, -1)]
    [InlineData(119, 3, 8, 8, 513, 513, +1, +1)]
    // 12+12 region (4 entries, all sign permutations).
    [InlineData(120, 4, 12, 12, 0, 0, -1, -1)]
    [InlineData(121, 4, 12, 12, 0, 0, +1, -1)]
    [InlineData(122, 4, 12, 12, 0, 0, -1, +1)]
    [InlineData(123, 4, 12, 12, 0, 0, +1, +1)]
    // 16+16 region (4 entries).
    [InlineData(124, 5, 16, 16, 0, 0, -1, -1)]
    [InlineData(125, 5, 16, 16, 0, 0, +1, -1)]
    [InlineData(126, 5, 16, 16, 0, 0, -1, +1)]
    [InlineData(127, 5, 16, 16, 0, 0, +1, +1)]
    public void Entry_matches_spec(int index, int byteCount, int xBits, int yBits, int deltaX, int deltaY, int xSign, int ySign)
    {
        var e = WoffTwoTripletTable.Entries[index];
        Assert.Equal(byteCount, e.ByteCount);
        Assert.Equal(xBits, e.XBits);
        Assert.Equal(yBits, e.YBits);
        Assert.Equal(deltaX, e.DeltaX);
        Assert.Equal(deltaY, e.DeltaY);
        Assert.Equal(xSign, e.XSign);
        Assert.Equal(ySign, e.YSign);
    }

    [Fact]
    public void Every_entry_has_known_byte_count()
    {
        // Per spec, byteCount ∈ {2, 3, 4, 5}.
        foreach (var e in WoffTwoTripletTable.Entries)
        {
            Assert.InRange(e.ByteCount, (byte)2, (byte)5);
        }
    }

    [Fact]
    public void Every_entry_has_signs_in_minus_one_zero_plus_one()
    {
        foreach (var e in WoffTwoTripletTable.Entries)
        {
            Assert.InRange(e.XSign, (sbyte)-1, (sbyte)1);
            Assert.InRange(e.YSign, (sbyte)-1, (sbyte)1);
        }
    }
}
