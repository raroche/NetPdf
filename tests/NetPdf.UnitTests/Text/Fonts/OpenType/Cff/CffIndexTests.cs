// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.OpenType.Cff;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType.Cff;

public sealed class CffIndexTests
{
    [Fact]
    public void Empty_index_serializes_to_two_bytes()
    {
        var bytes = SyntheticCff.BuildIndex(Array.Empty<byte[]>());
        Assert.Equal(2, bytes.Length);
        Assert.Equal((byte)0, bytes[0]);
        Assert.Equal((byte)0, bytes[1]);

        var index = CffIndex.Parse(bytes, bytes.AsMemory(), 0);
        Assert.Equal(0, index.Count);
        Assert.True(index.IsEmpty);
        Assert.Equal(2, index.TotalSize);
    }

    [Fact]
    public void Parse_three_object_index_returns_each_object_bytes()
    {
        var objects = new[]
        {
            new byte[] { 0xAA, 0xBB },
            new byte[] { 0xCC },
            new byte[] { 0xDD, 0xEE, 0xFF },
        };
        var bytes = SyntheticCff.BuildIndex(objects);
        var index = CffIndex.Parse(bytes, bytes.AsMemory(), 0);

        Assert.Equal(3, index.Count);
        Assert.Equal(bytes.Length, index.TotalSize);
        Assert.Equal(objects[0], index.GetObjectBytes(0).ToArray());
        Assert.Equal(objects[1], index.GetObjectBytes(1).ToArray());
        Assert.Equal(objects[2], index.GetObjectBytes(2).ToArray());
    }

    [Fact]
    public void GetObjectBytes_throws_when_index_out_of_range()
    {
        var bytes = SyntheticCff.BuildIndex(new[] { new byte[] { 1 } });
        var index = CffIndex.Parse(bytes, bytes.AsMemory(), 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => index.GetObjectBytes(1).ToArray());
    }

    [Fact]
    public void Parse_throws_on_truncated_input()
    {
        Assert.Throws<InvalidDataException>(() => CffIndex.Parse(new byte[] { 0 }, ReadOnlyMemory<byte>.Empty, 0));
    }

    [Fact]
    public void Parse_throws_when_offSize_outside_spec_range()
    {
        // count=1, offSize=5 (invalid)
        var bytes = new byte[] { 0, 1, 5, 0, 0, 0, 0, 0 };
        Assert.Throws<InvalidDataException>(() => CffIndex.Parse(bytes, bytes.AsMemory(), 0));
    }

    [Fact]
    public void Parse_throws_when_first_offset_is_not_one()
    {
        // count=1, offSize=1, offsets [2, 3], data [0xAA]
        var bytes = new byte[] { 0, 1, 1, 2, 3, 0xAA };
        Assert.Throws<InvalidDataException>(() => CffIndex.Parse(bytes, bytes.AsMemory(), 0));
    }

    [Fact]
    public void Parse_throws_when_offsets_decrease()
    {
        // count=2, offSize=1, offsets [1, 3, 2] (third decreases)
        var bytes = new byte[] { 0, 2, 1, 1, 3, 2, 0xAA, 0xBB };
        Assert.Throws<InvalidDataException>(() => CffIndex.Parse(bytes, bytes.AsMemory(), 0));
    }

    [Fact]
    public void Parse_throws_when_data_block_truncated()
    {
        // count=1, offSize=1, offsets [1, 5] → 4 bytes of data expected, but only 2 supplied
        var bytes = new byte[] { 0, 1, 1, 1, 5, 0xAA, 0xBB };
        Assert.Throws<InvalidDataException>(() => CffIndex.Parse(bytes, bytes.AsMemory(), 0));
    }
}
