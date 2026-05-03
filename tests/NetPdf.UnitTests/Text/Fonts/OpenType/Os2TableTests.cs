// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType;

public sealed class Os2TableTests
{
    [Fact]
    public void Parse_v4_includes_x_and_cap_height()
    {
        var os2 = Os2Table.Parse(SyntheticFont.Os2Bytes());
        Assert.Equal((ushort)4, os2.Version);
        Assert.Equal((short)500, os2.SxHeight);
        Assert.Equal((short)700, os2.SCapHeight);
        Assert.Equal((short)800, os2.STypoAscender);
        Assert.Equal((short)-200, os2.STypoDescender);
        Assert.Equal((ushort)400, os2.UsWeightClass);
    }

    [Fact]
    public void Parse_v0_omits_v1_and_v2_fields()
    {
        var bytes = new byte[78];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], 400);
        var os2 = Os2Table.Parse(bytes);
        Assert.Equal((ushort)0, os2.Version);
        Assert.Null(os2.UlCodePageRange1);
        Assert.Null(os2.SxHeight);
        Assert.Null(os2.SCapHeight);
    }

    [Fact]
    public void Parse_throws_on_unsupported_version()
    {
        var bytes = SyntheticFont.Os2Bytes();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0, 2), 6);
        Assert.Throws<InvalidDataException>(() => Os2Table.Parse(bytes));
    }

    [Fact]
    public void Parse_throws_when_v2_truncated()
    {
        // version = 2 but only 86 bytes — should throw because v2 needs 96
        var bytes = new byte[86];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0, 2), 2);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), 400);
        Assert.Throws<InvalidDataException>(() => Os2Table.Parse(bytes));
    }
}
