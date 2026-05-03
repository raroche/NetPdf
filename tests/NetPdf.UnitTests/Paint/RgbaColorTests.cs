// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Paint;
using Xunit;

namespace NetPdf.UnitTests.Paint;

public sealed class RgbaColorTests
{
    [Fact]
    public void Channel_constructor_packs_in_RRGGBBAA_order()
    {
        var c = new RgbaColor(0x12, 0x34, 0x56, 0x78);
        Assert.Equal((byte)0x12, c.R);
        Assert.Equal((byte)0x34, c.G);
        Assert.Equal((byte)0x56, c.B);
        Assert.Equal((byte)0x78, c.A);
        Assert.Equal(0x12345678u, c.Packed);
    }

    [Fact]
    public void Default_alpha_is_opaque()
    {
        var c = new RgbaColor(10, 20, 30);
        Assert.Equal((byte)255, c.A);
    }

    [Fact]
    public void Packed_constructor_round_trips_to_channels()
    {
        var c = new RgbaColor(0xAABBCCDDu);
        Assert.Equal((byte)0xAA, c.R);
        Assert.Equal((byte)0xBB, c.G);
        Assert.Equal((byte)0xCC, c.B);
        Assert.Equal((byte)0xDD, c.A);
    }

    [Fact]
    public void ToNormalizedRgb_divides_by_255()
    {
        var c = new RgbaColor(255, 128, 0);
        var (r, g, b) = c.ToNormalizedRgb();
        Assert.Equal(1.0, r);
        Assert.Equal(128.0 / 255.0, g);
        Assert.Equal(0.0, b);
    }

    [Fact]
    public void NormalizedAlpha_divides_alpha_by_255()
    {
        Assert.Equal(0.0, new RgbaColor(0, 0, 0, 0).NormalizedAlpha);
        Assert.Equal(1.0, new RgbaColor(0, 0, 0, 255).NormalizedAlpha);
        Assert.Equal(128.0 / 255.0, new RgbaColor(0, 0, 0, 128).NormalizedAlpha);
    }

    [Fact]
    public void Predefined_colors_match_expected_channels()
    {
        Assert.Equal(new RgbaColor(0, 0, 0), RgbaColor.Black);
        Assert.Equal(new RgbaColor(255, 255, 255), RgbaColor.White);
        Assert.Equal((byte)0, RgbaColor.Transparent.A);
    }

    [Fact]
    public void Equality_is_by_packed_value()
    {
        Assert.Equal(new RgbaColor(10, 20, 30, 40), new RgbaColor(10, 20, 30, 40));
        Assert.NotEqual(new RgbaColor(10, 20, 30, 40), new RgbaColor(10, 20, 30, 41));
        Assert.True(new RgbaColor(1, 2, 3) == new RgbaColor(1, 2, 3));
        Assert.True(new RgbaColor(1, 2, 3) != new RgbaColor(4, 5, 6));
    }

    [Fact]
    public void ToString_emits_uppercase_hex_with_alpha()
    {
        Assert.Equal("#0A14FFC0", new RgbaColor(0x0A, 0x14, 0xFF, 0xC0).ToString());
    }
}
