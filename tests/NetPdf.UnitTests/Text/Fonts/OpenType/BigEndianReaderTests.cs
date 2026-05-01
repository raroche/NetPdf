// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType;

public sealed class BigEndianReaderTests
{
    [Fact]
    public void ReadUInt16_decodes_big_endian_pair()
    {
        ReadOnlySpan<byte> bytes = [0x12, 0x34];
        var reader = new BigEndianReader(bytes);
        Assert.Equal((ushort)0x1234, reader.ReadUInt16());
        Assert.Equal(2, reader.Position);
    }

    [Fact]
    public void ReadInt16_decodes_negative_values()
    {
        ReadOnlySpan<byte> bytes = [0xFF, 0xFE];
        var reader = new BigEndianReader(bytes);
        Assert.Equal((short)-2, reader.ReadInt16());
    }

    [Fact]
    public void ReadUInt32_decodes_big_endian_quartet()
    {
        ReadOnlySpan<byte> bytes = [0xDE, 0xAD, 0xBE, 0xEF];
        var reader = new BigEndianReader(bytes);
        Assert.Equal(0xDEADBEEFu, reader.ReadUInt32());
    }

    [Fact]
    public void ReadInt64_decodes_big_endian_octet()
    {
        ReadOnlySpan<byte> bytes = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x2A];
        var reader = new BigEndianReader(bytes);
        Assert.Equal(42L, reader.ReadInt64());
    }

    [Fact]
    public void ReadBytes_advances_position_and_returns_slice()
    {
        ReadOnlySpan<byte> bytes = [1, 2, 3, 4, 5];
        var reader = new BigEndianReader(bytes);
        var slice = reader.ReadBytes(3);
        Assert.Equal(3, reader.Position);
        Assert.Equal(3, slice.Length);
        Assert.Equal((byte)1, slice[0]);
        Assert.Equal((byte)3, slice[2]);
    }

    [Fact]
    public void Skip_advances_position()
    {
        ReadOnlySpan<byte> bytes = [1, 2, 3, 4];
        var reader = new BigEndianReader(bytes);
        reader.Skip(2);
        Assert.Equal(2, reader.Position);
        Assert.Equal((byte)3, reader.ReadUInt8());
    }

    [Fact]
    public void Seek_sets_absolute_position()
    {
        ReadOnlySpan<byte> bytes = [1, 2, 3, 4];
        var reader = new BigEndianReader(bytes);
        reader.Skip(3);
        reader.Seek(1);
        Assert.Equal((byte)2, reader.ReadUInt8());
    }

    [Fact]
    public void Reading_past_end_throws_invalid_data()
    {
        var bytes = new byte[] { 1 };
        Assert.Throws<InvalidDataException>(() =>
        {
            var r = new BigEndianReader(bytes);
            r.ReadUInt16();
        });
    }

    [Fact]
    public void Seek_past_end_throws_argument_out_of_range()
    {
        var bytes = new byte[] { 1, 2 };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var r = new BigEndianReader(bytes);
            r.Seek(5);
        });
    }

    [Fact]
    public void Slice_returns_sub_span_without_moving_position()
    {
        ReadOnlySpan<byte> bytes = [1, 2, 3, 4, 5];
        var reader = new BigEndianReader(bytes);
        reader.Skip(1);
        var slice = reader.Slice(0, 4);
        Assert.Equal(4, slice.Length);
        Assert.Equal((byte)1, slice[0]);
        Assert.Equal(1, reader.Position);
    }

    [Fact]
    public void Slice_out_of_bounds_throws()
    {
        var bytes = new byte[] { 1, 2, 3 };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var r = new BigEndianReader(bytes);
            r.Slice(2, 5);
        });
    }
}
