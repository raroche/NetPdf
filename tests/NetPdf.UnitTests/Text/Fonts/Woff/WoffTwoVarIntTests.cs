// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.Woff;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.Woff;

/// <summary>
/// Per-encoding tests for the WOFF 2.0 variable-length integer readers
/// (<c>UIntBase128</c> per §4.1, <c>255UInt16</c> per §6.1.1). Both are exercised against
/// hand-encoded byte sequences so the spec invariants are pinned without relying on a
/// real WOFF 2.0 file.
/// </summary>
public sealed class WoffTwoVarIntTests
{
    // ───── UIntBase128 ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(new byte[] { 0x00 }, 0u)]
    [InlineData(new byte[] { 0x01 }, 1u)]
    [InlineData(new byte[] { 0x7F }, 127u)]
    [InlineData(new byte[] { 0x81, 0x00 }, 128u)]
    [InlineData(new byte[] { 0x81, 0x01 }, 129u)]
    [InlineData(new byte[] { 0xFF, 0x7F }, 16_383u)]
    [InlineData(new byte[] { 0x81, 0x80, 0x00 }, 16_384u)]
    public void UIntBase128_decodes_known_values(byte[] bytes, uint expected)
    {
        var cursor = 0;
        var v = WoffTwoVarInt.ReadUIntBase128(bytes, ref cursor);
        Assert.Equal(expected, v);
        Assert.Equal(bytes.Length, cursor);
    }

    [Fact]
    public void UIntBase128_rejects_leading_0x80_per_spec()
    {
        // Per §4.1: a leading byte of 0x80 would represent a leading zero — invalid.
        var bytes = new byte[] { 0x80, 0x01 };
        var cursor = 0;
        Assert.Throws<InvalidDataException>(() => WoffTwoVarInt.ReadUIntBase128(bytes, ref cursor));
    }

    [Fact]
    public void UIntBase128_rejects_sequences_longer_than_5_bytes()
    {
        // Six continuation bytes — invalid per spec.
        var bytes = new byte[] { 0x81, 0x81, 0x81, 0x81, 0x81, 0x81 };
        var cursor = 0;
        Assert.Throws<InvalidDataException>(() => WoffTwoVarInt.ReadUIntBase128(bytes, ref cursor));
    }

    [Fact]
    public void UIntBase128_rejects_overflow_beyond_uint32()
    {
        // The 5-byte form 0x90 0x80 0x80 0x80 0x00 would yield a 33-bit value (overflow).
        var bytes = new byte[] { 0x90, 0x80, 0x80, 0x80, 0x00 };
        var cursor = 0;
        Assert.Throws<InvalidDataException>(() => WoffTwoVarInt.ReadUIntBase128(bytes, ref cursor));
    }

    [Fact]
    public void UIntBase128_rejects_truncation_mid_sequence()
    {
        var bytes = new byte[] { 0x81 }; // continuation bit set but no more bytes
        var cursor = 0;
        Assert.Throws<InvalidDataException>(() => WoffTwoVarInt.ReadUIntBase128(bytes, ref cursor));
    }

    // ───── 255UInt16 ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(new byte[] { 0x00 }, (ushort)0)]
    [InlineData(new byte[] { 0x7F }, (ushort)127)]
    [InlineData(new byte[] { 0xFC }, (ushort)252)]
    [InlineData(new byte[] { 0xFF, 0x00 }, (ushort)253)]   // oneMoreByte1: 253 + 0
    [InlineData(new byte[] { 0xFF, 0xFF }, (ushort)508)]   // 253 + 255
    [InlineData(new byte[] { 0xFE, 0x00 }, (ushort)506)]   // oneMoreByte2: 506 + 0
    [InlineData(new byte[] { 0xFE, 0xFF }, (ushort)761)]   // 506 + 255
    [InlineData(new byte[] { 0xFD, 0x12, 0x34 }, (ushort)0x1234)]
    public void Read255UInt16_decodes_known_values(byte[] bytes, ushort expected)
    {
        var cursor = 0;
        var v = WoffTwoVarInt.Read255UInt16(bytes, ref cursor);
        Assert.Equal(expected, v);
        Assert.Equal(bytes.Length, cursor);
    }

    [Fact]
    public void Read255UInt16_rejects_truncation_after_oneMoreByte_marker()
    {
        var bytes = new byte[] { 0xFF };
        var cursor = 0;
        Assert.Throws<InvalidDataException>(() => WoffTwoVarInt.Read255UInt16(bytes, ref cursor));
    }

    [Fact]
    public void Read255UInt16_rejects_truncation_mid_word_form()
    {
        var bytes = new byte[] { 0xFD, 0x12 }; // word form needs 2 trailing bytes
        var cursor = 0;
        Assert.Throws<InvalidDataException>(() => WoffTwoVarInt.Read255UInt16(bytes, ref cursor));
    }
}
