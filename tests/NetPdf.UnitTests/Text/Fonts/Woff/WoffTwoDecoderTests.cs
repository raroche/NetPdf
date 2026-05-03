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
    public void Decode_rejects_truncated_compressed_block_with_InvalidDataException()
    {
        var bytes = SyntheticWoffTwo.BuildNullTransform();
        // Drop the last 4 bytes — the Brotli stream is now truncated.
        var truncated = bytes.AsSpan(0, bytes.Length - 4).ToArray();
        // Patch header.length so the layout validator's exact-match check doesn't fire first.
        BinaryPrimitives.WriteUInt32BigEndian(truncated.AsSpan(8, 4), (uint)truncated.Length);
        // The truncated compressed payload should fail with InvalidDataException at one of
        // the trust boundaries (Brotli decompression OR bounded-decompression check).
        Assert.Throws<InvalidDataException>(() => WoffTwoDecoder.Decode(truncated));
    }

    // ───── Trust-boundary tests (review follow-up #6) ────────────────────────

    [Fact]
    public void Decode_rejects_header_length_smaller_than_actual_buffer()
    {
        var bytes = SyntheticWoffTwo.BuildNullTransform();
        // Patch header.length to (actual - 4) — bytes are intact but the header lies about size.
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), (uint)(bytes.Length - 4));
        Assert.Throws<InvalidDataException>(() => WoffTwoDecoder.Decode(bytes));
    }

    [Fact]
    public void Decode_rejects_header_length_larger_than_actual_buffer()
    {
        var bytes = SyntheticWoffTwo.BuildNullTransform();
        // Patch header.length to (actual + 100) — header claims more bytes exist than really do.
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), (uint)(bytes.Length + 100));
        Assert.Throws<InvalidDataException>(() => WoffTwoDecoder.Decode(bytes));
    }

    [Fact]
    public void Decode_rejects_metadata_block_overlapping_compressed_data()
    {
        var bytes = SyntheticWoffTwo.BuildNullTransform();
        // Place metadata block at offset 60 — well within the compressed-data region.
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(28, 4), 60u);   // metaOffset
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(32, 4), 16u);   // metaLength
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(36, 4), 16u);   // metaOrigLength
        Assert.Throws<InvalidDataException>(() => WoffTwoDecoder.Decode(bytes));
    }

    [Fact]
    public void Decode_rejects_private_data_block_overlapping_compressed_data()
    {
        var bytes = SyntheticWoffTwo.BuildNullTransform();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(40, 4), 60u);   // privOffset (inside compressed)
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(44, 4), 16u);   // privLength
        Assert.Throws<InvalidDataException>(() => WoffTwoDecoder.Decode(bytes));
    }

    [Fact]
    public void Decode_rejects_metadata_offset_not_4_byte_aligned()
    {
        var bytes = SyntheticWoffTwo.BuildNullTransform();
        // Place metadata at the very end (offset = file length), off-alignment.
        var origLen = bytes.Length;
        Array.Resize(ref bytes, origLen + 16);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), (uint)bytes.Length);     // patch length to new size
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(28, 4), (uint)(origLen + 1));   // unaligned offset
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(32, 4), 8u);                    // metaLength
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(36, 4), 8u);                    // metaOrigLength
        Assert.Throws<InvalidDataException>(() => WoffTwoDecoder.Decode(bytes));
    }

    [Fact]
    public void Decode_rejects_duplicate_table_tags()
    {
        // Construct a synthetic WOFF2 by hand with two directory entries pointing at the
        // same tag (head, tagIndex=1). The directory parser should reject before the
        // pipeline ever reaches Brotli.
        var headerSize = 48;
        var dir = new byte[]
        {
            (byte)((0 << 6) | 1),  // flags: known-tag-index = 1 (head), null transform
            8,                      // origLength = 8
            (byte)((0 << 6) | 1),  // flags: same tag index
            8,                      // origLength = 8
        };
        var bytes = new byte[headerSize + dir.Length + 16];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), WoffTwoConstants.Signature);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), WoffTwoConstants.FlavorTrueType);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12, 2), 2);  // numTables
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(14, 2), 0);  // reserved
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), 8u); // totalCompressedSize
        dir.CopyTo(bytes.AsSpan(headerSize));
        var ex = Assert.Throws<InvalidDataException>(() => WoffTwoDecoder.Decode(bytes));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_accepts_null_transform_with_non_adjacent_glyf_and_loca()
    {
        // WOFF 2.0 §5.1: glyf/loca adjacency is required ONLY for the transformed pair
        // (version 0). With null transforms (version 3) the directory may freely interleave
        // other tables between them — a real WOFF2 file produced by a strict tag-ascending
        // encoder would naturally place head/hhea/hmtx/maxp between glyf and loca.
        var woff2 = SyntheticWoffTwo.BuildNullTransformNonAdjacentGlyfLoca();
        var sfnt = WoffTwoDecoder.Decode(woff2);
        // Must round-trip cleanly through the full OpenType pipeline.
        var font = NetPdf.Text.Fonts.OpenType.OpenTypeFont.Parse(sfnt);
        Assert.True(font.HasTrueTypeOutlines);
        Assert.Equal(SyntheticFont.NumGlyphs, font.Maxp.NumGlyphs);
    }

    [Fact]
    public void Decode_rejects_brotli_stream_producing_more_than_expected_size()
    {
        // Build a WOFF2 with a directory that declares small transformLengths but whose
        // Brotli stream actually decompresses to more bytes than the sum claims. We do
        // this by patching the directory's UIntBase128 origLength values down to a
        // smaller value than the real bytes — directory says "16 bytes total" but the
        // Brotli stream materializes the original full SFNT (much larger). The bounded
        // decompressor must reject before the file produces its final allocation.
        var bytes = SyntheticWoffTwo.BuildNullTransform();
        // Find the FIRST UIntBase128 origLength in the directory (header[12..14] = numTables;
        // directory starts at offset 48). Patch the first entry's origLength byte to 0
        // (claims 0 bytes). The decompressor will then expect total = sum of declared
        // lengths and find the actual stream is larger.
        // Easier: patch every UIntBase128 in the directory to declare length 0.
        // Find approximate directory bounds: starts at 48, ends at the compressed-data
        // start. We don't have that exposed, but we can patch the first byte after each
        // flags-byte to 0 (the simple-form UIntBase128 of zero is one byte 0x00).
        // For the SyntheticFont's 10 tables, the directory layout is:
        //   [flags][origLength UIntBase128]  — 2 bytes per entry when origLength fits in 7 bits
        // Patch every other byte starting at offset 49 (the origLength byte for entry 0).
        // This is fragile but adequate for proving the bounded decompressor catches the
        // overflow.
        for (var i = 49; i < 48 + 30; i += 2)
        {
            bytes[i] = 0x00;  // origLength = 0
        }
        Assert.Throws<InvalidDataException>(() => WoffTwoDecoder.Decode(bytes));
    }
}
