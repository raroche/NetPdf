// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Pdf.Fonts;
using NetPdf.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Fonts;

public sealed class SfntEnvelopeBuilderTests
{
    [Fact]
    public void BuildTtf_throws_when_tables_set_is_empty()
    {
        Assert.Throws<ArgumentException>(() => SfntEnvelopeBuilder.BuildTtf(new Dictionary<uint, ReadOnlyMemory<byte>>()));
    }

    [Fact]
    public void BuildTtf_throws_when_head_table_missing()
    {
        var tables = new Dictionary<uint, ReadOnlyMemory<byte>>
        {
            { OpenTypeTags.Maxp, new byte[6] },
        };
        Assert.Throws<ArgumentException>(() => SfntEnvelopeBuilder.BuildTtf(tables));
    }

    [Fact]
    public void BuildTtf_emits_TTF_scaler_type_and_correct_table_count()
    {
        var tables = MinimalTablesForChecksumTest();
        var bytes = SfntEnvelopeBuilder.BuildTtf(tables);

        var scaler = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4));
        Assert.Equal(SfntEnvelopeBuilder.TtfScalerType, scaler);

        var numTables = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4, 2));
        Assert.Equal((ushort)tables.Count, numTables);
    }

    [Fact]
    public void BuildTtf_writes_directory_entries_in_ascending_tag_order()
    {
        var tables = MinimalTablesForChecksumTest();
        var bytes = SfntEnvelopeBuilder.BuildTtf(tables);

        // Directory starts at offset 12, 16 bytes per entry. Tags should be ascending.
        var lastTag = 0u;
        for (var i = 0; i < tables.Count; i++)
        {
            var entryStart = SfntEnvelopeBuilder.SfntHeaderSize + (i * SfntEnvelopeBuilder.DirectoryRecordSize);
            var tag = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entryStart, 4));
            Assert.True(tag > lastTag, $"Tag at directory index {i} ({tag:X8}) is not greater than the previous ({lastTag:X8}).");
            lastTag = tag;
        }
    }

    [Fact]
    public void BuildTtf_pads_each_table_to_4_byte_alignment()
    {
        // Build with one table whose declared length is 3 bytes — the next-table offset
        // should be 4-byte-aligned past that.
        var tinyHead = MakeFakeHead();
        var tinyMaxp = new byte[5]; // 5 bytes — needs 3 bytes of padding
        var tables = new Dictionary<uint, ReadOnlyMemory<byte>>
        {
            { OpenTypeTags.Head, tinyHead },
            { OpenTypeTags.Maxp, tinyMaxp },
        };
        var bytes = SfntEnvelopeBuilder.BuildTtf(tables);

        // Directory has 2 entries; second entry's offset (uint32 at directory[1].offset)
        // should equal first.offset + AlignTo4(first.length).
        var firstOffset = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(SfntEnvelopeBuilder.SfntHeaderSize + 8, 4));
        var firstLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(SfntEnvelopeBuilder.SfntHeaderSize + 12, 4));
        var secondOffset = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(SfntEnvelopeBuilder.SfntHeaderSize + SfntEnvelopeBuilder.DirectoryRecordSize + 8, 4));
        var paddedFirst = (firstLength + 3) & ~3u;
        Assert.Equal(firstOffset + paddedFirst, secondOffset);
    }

    [Fact]
    public void BuildTtf_patches_head_checkSumAdjustment()
    {
        var tables = MinimalTablesForChecksumTest();
        var bytes = SfntEnvelopeBuilder.BuildTtf(tables);

        // Find head's offset via the directory.
        var headOffset = FindTableOffset(bytes, OpenTypeTags.Head);
        var checkSumAdj = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(headOffset + 8, 4));

        // Recompute file checksum with checkSumAdjustment temporarily zero, then
        // 0xB1B0AFBA - that should equal checkSumAdj.
        var copy = bytes.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(headOffset + 8, 4), 0);
        var fileChecksum = ComputeChecksum(copy);
        var expected = SfntEnvelopeBuilder.CheckSumAdjustmentMagic - fileChecksum;
        Assert.Equal(expected, checkSumAdj);
    }

    [Fact]
    public void BuildTtf_is_byte_deterministic()
    {
        var tables = MinimalTablesForChecksumTest();
        var a = SfntEnvelopeBuilder.BuildTtf(tables);
        var b = SfntEnvelopeBuilder.BuildTtf(tables);
        Assert.Equal(a, b);
    }

    private static Dictionary<uint, ReadOnlyMemory<byte>> MinimalTablesForChecksumTest()
    {
        return new Dictionary<uint, ReadOnlyMemory<byte>>
        {
            { OpenTypeTags.Head, MakeFakeHead() },
            { OpenTypeTags.Maxp, new byte[6] }, // anything; checksum just sums bytes
            { OpenTypeTags.Hhea, new byte[36] },
        };
    }

    /// <summary>Build a 54-byte head with the magic constant set; everything else can be zero.</summary>
    private static byte[] MakeFakeHead()
    {
        var head = new byte[54];
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(12, 4), HeadTable.MagicNumber);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18, 2), 1000); // unitsPerEm in valid range
        return head;
    }

    private static int FindTableOffset(byte[] sfnt, uint tag)
    {
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(4, 2));
        for (var i = 0; i < numTables; i++)
        {
            var entryStart = SfntEnvelopeBuilder.SfntHeaderSize + (i * SfntEnvelopeBuilder.DirectoryRecordSize);
            var entryTag = BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(entryStart, 4));
            if (entryTag == tag)
            {
                return (int)BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(entryStart + 8, 4));
            }
        }
        throw new InvalidOperationException($"Tag {tag:X8} not found.");
    }

    private static uint ComputeChecksum(ReadOnlySpan<byte> bytes)
    {
        var sum = 0u;
        for (var i = 0; i + 4 <= bytes.Length; i += 4)
        {
            sum += BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(i, 4));
        }
        return sum;
    }
}
