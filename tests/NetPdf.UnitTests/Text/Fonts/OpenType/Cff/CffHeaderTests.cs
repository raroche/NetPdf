// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.OpenType.Cff;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType.Cff;

public sealed class CffHeaderTests
{
    [Fact]
    public void Parse_decodes_v1_header()
    {
        var bytes = new byte[] { 1, 0, 4, 2 };
        var header = CffHeader.Parse(bytes);
        Assert.Equal((byte)1, header.Major);
        Assert.Equal((byte)0, header.Minor);
        Assert.Equal((byte)4, header.HdrSize);
        Assert.Equal((byte)2, header.OffSize);
    }

    [Fact]
    public void Parse_throws_on_unsupported_major_version()
    {
        var bytes = new byte[] { 2, 0, 5, 2 }; // CFF2 — not yet supported
        var ex = Assert.Throws<InvalidDataException>(() => CffHeader.Parse(bytes));
        Assert.Contains("CFF v1", ex.Message);
    }

    [Fact]
    public void Parse_throws_on_offSize_outside_spec_range()
    {
        Assert.Throws<InvalidDataException>(() => CffHeader.Parse(new byte[] { 1, 0, 4, 0 }));
        Assert.Throws<InvalidDataException>(() => CffHeader.Parse(new byte[] { 1, 0, 4, 5 }));
    }

    [Fact]
    public void Parse_throws_on_hdrSize_below_minimum()
    {
        Assert.Throws<InvalidDataException>(() => CffHeader.Parse(new byte[] { 1, 0, 3, 2 }));
    }

    [Fact]
    public void Parse_throws_when_hdrSize_exceeds_buffer()
    {
        Assert.Throws<InvalidDataException>(() => CffHeader.Parse(new byte[] { 1, 0, 100, 2 }));
    }

    [Fact]
    public void Parse_throws_on_truncated_input()
    {
        Assert.Throws<InvalidDataException>(() => CffHeader.Parse(new byte[] { 1, 0 }));
    }
}
