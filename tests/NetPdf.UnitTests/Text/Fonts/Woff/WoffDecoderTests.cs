// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.IO.Compression;
using NetPdf.Text.Fonts.Woff;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.Woff;

public sealed class WoffDecoderTests
{
    [Fact]
    public void Decode_produces_sfnt_with_TTF_scaler_magic()
    {
        var woffBytes = SyntheticWoff.Build();
        var sfnt = WoffDecoder.Decode(woffBytes);
        var sfntVersion = BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(0, 4));
        Assert.Equal(0x00010000u, sfntVersion);
    }

    [Fact]
    public void Decode_preserves_table_count_in_sfnt_header()
    {
        var woffBytes = SyntheticWoff.Build();
        var sfnt = WoffDecoder.Decode(woffBytes);
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(4, 2));
        Assert.Equal(10, numTables); // synthetic font has 10 tables
    }

    [Fact]
    public void Decode_directory_is_sorted_by_tag_ascending()
    {
        var woffBytes = SyntheticWoff.Build();
        var sfnt = WoffDecoder.Decode(woffBytes);
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(4, 2));
        const int sfntHeaderSize = 12;
        const int recordSize = 16;
        var prevTag = 0u;
        for (var i = 0; i < numTables; i++)
        {
            var tag = BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(sfntHeaderSize + (i * recordSize), 4));
            Assert.True(tag > prevTag, $"Directory not sorted ascending: 0x{tag:X8} after 0x{prevTag:X8}");
            prevTag = tag;
        }
    }

    [Fact]
    public void Decode_each_table_offset_is_4_byte_aligned()
    {
        var woffBytes = SyntheticWoff.Build();
        var sfnt = WoffDecoder.Decode(woffBytes);
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(4, 2));
        const int sfntHeaderSize = 12;
        const int recordSize = 16;
        for (var i = 0; i < numTables; i++)
        {
            var offset = BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(sfntHeaderSize + (i * recordSize) + 8, 4));
            Assert.Equal(0u, offset & 3u);
        }
    }

    [Fact]
    public void Decode_is_deterministic_for_byte_equal_input()
    {
        var woffBytes = SyntheticWoff.Build();
        var sfnt1 = WoffDecoder.Decode(woffBytes);
        var sfnt2 = WoffDecoder.Decode(woffBytes);
        Assert.Equal(sfnt1, sfnt2);
    }

    [Fact]
    public void Decode_reproduces_input_table_bytes_exactly()
    {
        // Build the WOFF, decode it, then verify that for one specific table the
        // decoded bytes match what SyntheticFont produced. We use 'head' since
        // SyntheticFont exposes HeadBytes and the table is required.
        var sfntInput = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.Build();
        var woffBytes = SyntheticWoff.Build(sfntInput);
        var sfntDecoded = WoffDecoder.Decode(woffBytes);

        var headBytesDecoded = ExtractTableBytes(sfntDecoded, 0x68656164u); // "head"

        // Strip checkSumAdjustment from the comparison — WoffDecoder repatches it.
        var headBytesInput = ExtractTableBytes(sfntInput, 0x68656164u);
        BinaryPrimitives.WriteUInt32BigEndian(headBytesInput.AsSpan(8, 4), 0u);
        BinaryPrimitives.WriteUInt32BigEndian(headBytesDecoded.AsSpan(8, 4), 0u);

        Assert.Equal(headBytesInput, headBytesDecoded);
    }

    [Fact]
    public void Decode_rejects_truncated_compressed_payload()
    {
        // Use BuildWithLargeCompressibleTable — the 'wxyz' auxiliary table of 4096 zero
        // bytes is guaranteed to be zlib-compressed (zlib shrinks all-zeros by ~99.5%),
        // so this test exercises the corruption path deterministically. Independent of
        // whether the synthetic font's small structural tables happen to compress.
        var woffBytes = SyntheticWoff.BuildWithLargeCompressibleTable();
        var header = WoffHeader.Parse(woffBytes);
        var entries = WoffTableEntry.ParseDirectory(woffBytes, header);
        var auxIdx = SyntheticWoff.FindDirectoryIndex(woffBytes, SyntheticWoff.AuxiliaryTableTag);
        Assert.True(auxIdx >= 0, "Auxiliary 'wxyz' table not found in compressible-table fixture.");
        var auxEntry = entries[auxIdx];
        Assert.False(auxEntry.IsStored, "Auxiliary table was stored uncompressed — fixture invariant violated.");

        // Wreck the zlib bytes by zeroing the first 2 (the zlib header CMF/FLG bytes).
        woffBytes.AsSpan((int)auxEntry.Offset, 2).Clear();
        Assert.Throws<InvalidDataException>(() => WoffDecoder.Decode(woffBytes));
    }

    [Fact]
    public void Decode_rejects_decompressed_length_mismatch()
    {
        // Bump origLength so the decompressor produces fewer bytes than declared.
        // We must also bump totalSfntSize in lock-step or the layout validator throws first
        // (which would be a different rejection path). The deterministic compressible
        // table fixture from BuildWithLargeCompressibleTable supplies a guaranteed-
        // compressed entry.
        var woffBytes = SyntheticWoff.BuildWithLargeCompressibleTable();
        var auxIdx = SyntheticWoff.FindDirectoryIndex(woffBytes, SyntheticWoff.AuxiliaryTableTag);
        Assert.True(auxIdx >= 0);
        var recordOffset = WoffHeader.HeaderSize + (auxIdx * WoffTableEntry.RecordSize);
        var origLengthBefore = BinaryPrimitives.ReadUInt32BigEndian(woffBytes.AsSpan(recordOffset + 12, 4));
        const uint bump = 1024;

        // Update origLength.
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan(recordOffset + 12, 4), origLengthBefore + bump);

        // Update totalSfntSize to match (must stay multiple of 4 too — bump=1024 satisfies that).
        var totalSfntSizeBefore = BinaryPrimitives.ReadUInt32BigEndian(woffBytes.AsSpan(16, 4));
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan(16, 4), totalSfntSizeBefore + bump);

        var ex = Assert.Throws<InvalidDataException>(() => WoffDecoder.Decode(woffBytes));
        Assert.Contains("decompression", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_handles_stored_tables_without_decompression()
    {
        // Build a synthetic SFNT, then synthesize a WOFF where every table is stored
        // (compLength == origLength). We do this by wrapping the synthetic font and
        // overriding the compressed bytes with the original — bypassing
        // SyntheticWoff.Build's compression-attempt path is easier to test by just
        // confirming the round-trip works on the smallest tables, which the synthetic
        // font already encodes uncompressed.
        var woffBytes = SyntheticWoff.Build();
        var header = WoffHeader.Parse(woffBytes);
        var entries = WoffTableEntry.ParseDirectory(woffBytes, header);

        // At least one of the synthetic font's tiny tables (e.g., maxp at ~32 bytes)
        // should fail to compress and be stored. Confirm this is the case.
        var atLeastOneStored = false;
        foreach (var entry in entries)
        {
            if (entry.IsStored)
            {
                atLeastOneStored = true;
                break;
            }
        }
        Assert.True(atLeastOneStored, "Expected at least one stored (uncompressed) table in synthetic WOFF.");

        // The decode-and-it-works baseline test (Decode_produces_sfnt_with_TTF_scaler_magic)
        // already exercises the stored path implicitly. This test asserts the existence
        // of stored tables in the input, locking in that assumption.
        var sfnt = WoffDecoder.Decode(woffBytes);
        Assert.NotNull(sfnt);
    }

    private static byte[] ExtractTableBytes(byte[] sfnt, uint tag)
    {
        const int sfntHeaderSize = 12;
        const int recordSize = 16;
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(4, 2));
        for (var i = 0; i < numTables; i++)
        {
            var recordOffset = sfntHeaderSize + (i * recordSize);
            var entryTag = BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(recordOffset, 4));
            if (entryTag == tag)
            {
                var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(recordOffset + 8, 4));
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(recordOffset + 12, 4));
                return sfnt.AsSpan(offset, length).ToArray();
            }
        }
        throw new InvalidOperationException($"Tag 0x{tag:X8} not found in SFNT directory.");
    }
}
