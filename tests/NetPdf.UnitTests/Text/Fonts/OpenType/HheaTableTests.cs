// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType;

public sealed class HheaTableTests
{
    [Fact]
    public void Parse_decodes_typo_metrics_and_numberOfHMetrics()
    {
        var hhea = HheaTable.Parse(SyntheticFont.HheaBytes());
        Assert.Equal((short)800, hhea.Ascender);
        Assert.Equal((short)-200, hhea.Descender);
        Assert.Equal((short)100, hhea.LineGap);
        Assert.Equal(SyntheticFont.NumberOfHMetrics, hhea.NumberOfHMetrics);
    }

    [Fact]
    public void Parse_throws_when_numberOfHMetrics_is_zero()
    {
        var bytes = SyntheticFont.HheaBytes();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(34, 2), 0);
        Assert.Throws<InvalidDataException>(() => HheaTable.Parse(bytes));
    }

    [Fact]
    public void Parse_throws_when_table_too_short()
    {
        var bytes = new byte[10];
        Assert.Throws<InvalidDataException>(() => HheaTable.Parse(bytes));
    }
}
