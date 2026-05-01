// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType;

public sealed class HeadTableTests
{
    [Fact]
    public void Parse_decodes_unitsPerEm_and_indexToLocFormat()
    {
        var head = HeadTable.Parse(SyntheticFont.HeadBytes());
        Assert.Equal(SyntheticFont.UnitsPerEm, head.UnitsPerEm);
        Assert.Equal((short)1, head.IndexToLocFormat); // long format
    }

    [Fact]
    public void Parse_throws_on_wrong_magic_number()
    {
        var bytes = SyntheticFont.HeadBytes();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12, 4), 0xDEADBEEFu);
        Assert.Throws<InvalidDataException>(() => HeadTable.Parse(bytes));
    }

    [Fact]
    public void Parse_throws_on_unitsPerEm_below_minimum()
    {
        var bytes = SyntheticFont.HeadBytes();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(18, 2), 8); // < 16
        Assert.Throws<InvalidDataException>(() => HeadTable.Parse(bytes));
    }

    [Fact]
    public void Parse_throws_on_unitsPerEm_above_maximum()
    {
        var bytes = SyntheticFont.HeadBytes();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(18, 2), 32_000); // > 16384
        Assert.Throws<InvalidDataException>(() => HeadTable.Parse(bytes));
    }

    [Fact]
    public void Parse_throws_on_invalid_indexToLocFormat()
    {
        var bytes = SyntheticFont.HeadBytes();
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(50, 2), 7);
        Assert.Throws<InvalidDataException>(() => HeadTable.Parse(bytes));
    }

    [Fact]
    public void Parse_throws_when_table_too_short()
    {
        var bytes = new byte[20];
        Assert.Throws<InvalidDataException>(() => HeadTable.Parse(bytes));
    }
}
