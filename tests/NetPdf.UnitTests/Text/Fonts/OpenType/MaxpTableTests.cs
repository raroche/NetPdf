// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType;

public sealed class MaxpTableTests
{
    [Fact]
    public void Parse_v10_decodes_full_field_set()
    {
        var maxp = MaxpTable.Parse(SyntheticFont.MaxpBytes());
        Assert.Equal(MaxpTable.Version10, maxp.Version);
        Assert.True(maxp.IsTrueTypeProfile);
        Assert.Equal(SyntheticFont.NumGlyphs, maxp.NumGlyphs);
        Assert.NotNull(maxp.MaxPoints);
    }

    [Fact]
    public void Parse_v05_decodes_only_numGlyphs()
    {
        var bytes = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), MaxpTable.Version05);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), 42);
        var maxp = MaxpTable.Parse(bytes);
        Assert.Equal(MaxpTable.Version05, maxp.Version);
        Assert.False(maxp.IsTrueTypeProfile);
        Assert.Equal((ushort)42, maxp.NumGlyphs);
        Assert.Null(maxp.MaxPoints);
    }

    [Fact]
    public void Parse_throws_on_unknown_version()
    {
        var bytes = SyntheticFont.MaxpBytes();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 0xCAFE0000u);
        Assert.Throws<InvalidDataException>(() => MaxpTable.Parse(bytes));
    }

    [Fact]
    public void Parse_throws_on_zero_numGlyphs()
    {
        var bytes = SyntheticFont.MaxpBytes();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), 0);
        Assert.Throws<InvalidDataException>(() => MaxpTable.Parse(bytes));
    }

    [Fact]
    public void Parse_throws_when_v10_truncated()
    {
        var truncated = new byte[10];
        BinaryPrimitives.WriteUInt32BigEndian(truncated.AsSpan(0, 4), MaxpTable.Version10);
        BinaryPrimitives.WriteUInt16BigEndian(truncated.AsSpan(4, 2), 1);
        Assert.Throws<InvalidDataException>(() => MaxpTable.Parse(truncated));
    }
}
