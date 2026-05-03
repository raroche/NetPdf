// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.Woff;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.Woff;

public sealed class WoffTableEntryTests
{
    [Fact]
    public void ParseDirectory_returns_one_entry_per_table()
    {
        var woffBytes = SyntheticWoff.Build();
        var header = WoffHeader.Parse(woffBytes);
        var entries = WoffTableEntry.ParseDirectory(woffBytes, header);
        Assert.Equal(header.NumTables, entries.Length);
    }

    [Fact]
    public void ParseDirectory_entries_carry_distinct_tags()
    {
        var woffBytes = SyntheticWoff.Build();
        var header = WoffHeader.Parse(woffBytes);
        var entries = WoffTableEntry.ParseDirectory(woffBytes, header);
        var tags = new HashSet<uint>();
        foreach (var entry in entries)
        {
            Assert.True(tags.Add(entry.Tag), $"Duplicate tag in synthetic WOFF: 0x{entry.Tag:X8}");
        }
    }

    [Fact]
    public void IsStored_true_when_compLength_equals_origLength()
    {
        var entry = new WoffTableEntry(Tag: 0x68656164, Offset: 0, CompLength: 50, OrigLength: 50, OrigChecksum: 0);
        Assert.True(entry.IsStored);
    }

    [Fact]
    public void IsStored_false_when_compLength_less_than_origLength()
    {
        var entry = new WoffTableEntry(Tag: 0x68656164, Offset: 0, CompLength: 30, OrigLength: 50, OrigChecksum: 0);
        Assert.False(entry.IsStored);
    }

    [Fact]
    public void ParseDirectory_rejects_compLength_greater_than_origLength()
    {
        var woffBytes = SyntheticWoff.Build();
        var header = WoffHeader.Parse(woffBytes);
        // Record 0 starts at offset 44. compLength is at +8 (uint32), origLength at +12.
        const int firstRecordOffset = 44;
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan(firstRecordOffset + 8, 4), 99999);
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan(firstRecordOffset + 12, 4), 100);
        var ex = Assert.Throws<InvalidDataException>(() => WoffTableEntry.ParseDirectory(woffBytes, header));
        Assert.Contains("compLength", ex.Message);
    }

    [Fact]
    public void ParseDirectory_rejects_offset_inside_directory_region()
    {
        var woffBytes = SyntheticWoff.Build();
        var header = WoffHeader.Parse(woffBytes);
        // Set the first record's offset to 50 — that's inside the directory range
        // (header is 44, directory starts at 44 and runs for numTables × 20 bytes).
        const int firstRecordOffset = 44;
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan(firstRecordOffset + 4, 4), 50);
        var ex = Assert.Throws<InvalidDataException>(() => WoffTableEntry.ParseDirectory(woffBytes, header));
        Assert.Contains("aliases header/directory", ex.Message);
    }

    [Fact]
    public void ParseDirectory_rejects_offset_plus_compLength_past_file_end()
    {
        var woffBytes = SyntheticWoff.Build();
        var header = WoffHeader.Parse(woffBytes);
        const int firstRecordOffset = 44;
        // Set offset just before the end + compLength way past the end.
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan(firstRecordOffset + 4, 4), (uint)woffBytes.Length - 10);
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan(firstRecordOffset + 8, 4), 50);
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan(firstRecordOffset + 12, 4), 50);
        var ex = Assert.Throws<InvalidDataException>(() => WoffTableEntry.ParseDirectory(woffBytes, header));
        Assert.Contains("extends past file length", ex.Message);
    }

    [Fact]
    public void ParseDirectory_rejects_duplicate_tag_via_strict_ascending_check()
    {
        var woffBytes = SyntheticWoff.Build();
        var header = WoffHeader.Parse(woffBytes);
        const int firstRecordOffset = 44;
        const int secondRecordOffset = firstRecordOffset + 20;
        // Copy the first record's tag into the second — duplicate tags fail the strict-
        // ascending check (which subsumes the previous duplicate-only check).
        var firstTag = BinaryPrimitives.ReadUInt32BigEndian(woffBytes.AsSpan(firstRecordOffset, 4));
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan(secondRecordOffset, 4), firstTag);
        var ex = Assert.Throws<InvalidDataException>(() => WoffTableEntry.ParseDirectory(woffBytes, header));
        Assert.Contains("ascending", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseDirectory_rejects_descending_tag_order()
    {
        var woffBytes = SyntheticWoff.Build();
        var header = WoffHeader.Parse(woffBytes);
        const int firstRecordOffset = 44;
        const int secondRecordOffset = firstRecordOffset + 20;
        // Swap first and second tags so the directory is no longer ascending.
        var firstTag = BinaryPrimitives.ReadUInt32BigEndian(woffBytes.AsSpan(firstRecordOffset, 4));
        var secondTag = BinaryPrimitives.ReadUInt32BigEndian(woffBytes.AsSpan(secondRecordOffset, 4));
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan(firstRecordOffset, 4), secondTag);
        BinaryPrimitives.WriteUInt32BigEndian(woffBytes.AsSpan(secondRecordOffset, 4), firstTag);
        var ex = Assert.Throws<InvalidDataException>(() => WoffTableEntry.ParseDirectory(woffBytes, header));
        Assert.Contains("ascending", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseDirectory_rejects_buffer_too_small_for_directory()
    {
        // Build a header that claims 5 tables but truncate the buffer right after it.
        var woffBytes = SyntheticWoff.Build();
        var header = WoffHeader.Parse(woffBytes);
        var truncated = woffBytes.AsSpan(0, WoffHeader.HeaderSize + 10).ToArray();
        var ex = Assert.Throws<InvalidDataException>(() => WoffTableEntry.ParseDirectory(truncated, header));
        Assert.Contains("buffer too small", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
