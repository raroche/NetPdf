// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.Woff;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.Woff;

public sealed class WoffHeaderTests
{
    [Fact]
    public void Parse_round_trips_all_fields_from_synthetic_woff()
    {
        var woffBytes = SyntheticWoff.Build();
        var header = WoffHeader.Parse(woffBytes);

        Assert.Equal(0x00010000u, header.Flavor);            // synthetic font is TTF
        Assert.Equal((uint)woffBytes.Length, header.Length); // builder fills length consistently
        Assert.Equal(10, header.NumTables);                  // synthetic font has 10 tables
        Assert.True(header.TotalSfntSize > 0);
        Assert.Equal(0u, header.MetaOffset);
        Assert.Equal(0u, header.MetaLength);
        Assert.Equal(0u, header.PrivOffset);
        Assert.Equal(0u, header.PrivLength);
    }

    [Fact]
    public void Parse_rejects_buffer_smaller_than_header()
    {
        var tooSmall = new byte[WoffHeader.HeaderSize - 1];
        Assert.Throws<InvalidDataException>(() => WoffHeader.Parse(tooSmall));
    }

    [Fact]
    public void Parse_rejects_bad_signature()
    {
        var woffBytes = SyntheticWoff.Build();
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan()[..4], 0xDEADBEEFu);
        var ex = Assert.Throws<InvalidDataException>(() => WoffHeader.Parse(woffBytes));
        Assert.Contains("signature", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_unrecognized_flavor()
    {
        var woffBytes = SyntheticWoff.Build();
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan()[4..8], 0x12345678u);
        var ex = Assert.Throws<InvalidDataException>(() => WoffHeader.Parse(woffBytes));
        Assert.Contains("flavor", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_accepts_otf_flavor()
    {
        var woffBytes = SyntheticWoff.Build();
        // 0x4F54544F = "OTTO". The synthetic font is structurally TTF; this only proves
        // WoffHeader.Parse accepts the OTF flavor at the header layer. Full OTF round-trip
        // through OpenTypeFont.Parse needs CFF tables and is exercised separately.
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan()[4..8], 0x4F54544Fu);
        var header = WoffHeader.Parse(woffBytes);
        Assert.Equal(0x4F54544Fu, header.Flavor);
    }

    [Fact]
    public void Parse_rejects_zero_numTables()
    {
        var woffBytes = SyntheticWoff.Build();
        BinaryPrimitives.WriteUInt16BigEndian(woffBytes.AsSpan()[12..14], 0);
        var ex = Assert.Throws<InvalidDataException>(() => WoffHeader.Parse(woffBytes));
        Assert.Contains("numTables", ex.Message);
    }

    [Fact]
    public void Parse_rejects_nonzero_reserved()
    {
        var woffBytes = SyntheticWoff.Build();
        BinaryPrimitives.WriteUInt16BigEndian(woffBytes.AsSpan()[14..16], 1);
        var ex = Assert.Throws<InvalidDataException>(() => WoffHeader.Parse(woffBytes));
        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_length_greater_than_buffer()
    {
        var woffBytes = SyntheticWoff.Build();
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan()[8..12], (uint)woffBytes.Length + 100);
        var ex = Assert.Throws<InvalidDataException>(() => WoffHeader.Parse(woffBytes));
        Assert.Contains("length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_metadata_offset_without_length()
    {
        var woffBytes = SyntheticWoff.Build();
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan()[24..28], 100); // metaOffset
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan()[28..32], 0);   // metaLength
        var ex = Assert.Throws<InvalidDataException>(() => WoffHeader.Parse(woffBytes));
        Assert.Contains("metadata", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_private_block_extending_past_file()
    {
        var woffBytes = SyntheticWoff.Build();
        // privOffset just before the end + privLength past the end.
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan()[36..40], (uint)woffBytes.Length - 10);
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan()[40..44], 100);
        var ex = Assert.Throws<InvalidDataException>(() => WoffHeader.Parse(woffBytes));
        Assert.Contains("private", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
