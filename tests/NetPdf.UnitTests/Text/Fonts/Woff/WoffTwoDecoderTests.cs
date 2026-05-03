// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.OpenType;
using NetPdf.Text.Fonts.Woff;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.Woff;

/// <summary>
/// End-to-end round-trip tests for <see cref="WoffTwoDecoder"/> using a synthetic WOFF 2.0
/// stream with all-null transforms. Phase A scope: confirms header walk, table directory
/// parse (including the <c>UIntBase128</c> path), Brotli decompression, null-transform
/// pass-through, and SFNT reassembly all work end-to-end. Glyf/loca transform reversal is
/// covered separately once Phase B lands.
/// </summary>
public sealed class WoffTwoDecoderTests
{
    [Fact]
    public void Decode_round_trips_synthetic_null_transform_WOFF2_to_a_parseable_TTF()
    {
        var woff2 = SyntheticWoffTwo.BuildNullTransform();
        var sfnt = WoffTwoDecoder.Decode(woff2);

        Assert.NotEmpty(sfnt);
        // Re-parse via the full OpenType pipeline — proves every table survived.
        var font = OpenTypeFont.Parse(sfnt);
        Assert.True(font.HasTrueTypeOutlines);
        Assert.Equal(SyntheticFont.NumGlyphs, font.Maxp.NumGlyphs);
        Assert.Equal(SyntheticFont.UnitsPerEm, font.Head.UnitsPerEm);
    }

    [Fact]
    public void Decode_emits_TrueType_scaler_magic_at_byte_zero()
    {
        var woff2 = SyntheticWoffTwo.BuildNullTransform();
        var sfnt = WoffTwoDecoder.Decode(woff2);
        var scaler = BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(0, 4));
        Assert.Equal(0x00010000u, scaler);
    }

    [Fact]
    public void Decode_writes_directory_in_tag_ascending_order()
    {
        var woff2 = SyntheticWoffTwo.BuildNullTransform();
        var sfnt = WoffTwoDecoder.Decode(woff2);
        var span = sfnt.AsSpan();
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(span[4..6]);
        const int sfntHeaderSize = 12;
        const int recordSize = 16;
        uint prev = 0;
        for (var i = 0; i < numTables; i++)
        {
            var rec = sfntHeaderSize + i * recordSize;
            var tag = BinaryPrimitives.ReadUInt32BigEndian(span[rec..(rec + 4)]);
            Assert.True(tag > prev, $"directory not ascending at index {i}: 0x{prev:X8} → 0x{tag:X8}");
            prev = tag;
        }
    }

    [Fact]
    public void Decode_aligns_every_table_offset_to_4_bytes()
    {
        var woff2 = SyntheticWoffTwo.BuildNullTransform();
        var sfnt = WoffTwoDecoder.Decode(woff2);
        var span = sfnt.AsSpan();
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(span[4..6]);
        const int sfntHeaderSize = 12;
        const int recordSize = 16;
        for (var i = 0; i < numTables; i++)
        {
            var rec = sfntHeaderSize + i * recordSize;
            var off = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(rec + 8)..(rec + 12)]);
            Assert.True((off & 3) == 0, $"table at index {i} has unaligned offset {off}");
        }
    }

    [Fact]
    public void Decode_is_deterministic()
    {
        var woff2 = SyntheticWoffTwo.BuildNullTransform();
        var first = WoffTwoDecoder.Decode(woff2);
        var second = WoffTwoDecoder.Decode(woff2);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Decode_rejects_TTC_collection_signature()
    {
        var bytes = SyntheticWoffTwo.BuildNullTransform();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), WoffTwoConstants.FlavorTtc);
        Assert.Throws<InvalidDataException>(() => WoffTwoDecoder.Decode(bytes));
    }

    [Fact]
    public void Decode_rejects_truncated_compressed_block()
    {
        var bytes = SyntheticWoffTwo.BuildNullTransform();
        // Drop the last 4 bytes — the Brotli stream is now truncated.
        var truncated = bytes.AsSpan(0, bytes.Length - 4).ToArray();
        // Patch length so the header doesn't fail length validation first.
        BinaryPrimitives.WriteUInt32BigEndian(truncated.AsSpan(8, 4), (uint)truncated.Length);
        Assert.ThrowsAny<Exception>(() => WoffTwoDecoder.Decode(truncated));
    }
}
