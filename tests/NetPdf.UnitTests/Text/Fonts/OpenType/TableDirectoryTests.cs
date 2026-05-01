// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType;

public sealed class TableDirectoryTests
{
    [Fact]
    public void Parse_synthetic_font_has_expected_table_count_and_flavor()
    {
        var bytes = SyntheticFont.Build();
        var dir = TableDirectory.Parse(bytes);

        Assert.Equal(OpenTypeTags.SfntVersionTtf, dir.SfntVersion);
        Assert.True(dir.IsTrueType);
        Assert.False(dir.IsCff);
        Assert.Equal((ushort)10, dir.NumTables);
    }

    [Fact]
    public void Parse_synthetic_font_indexes_every_required_table()
    {
        var bytes = SyntheticFont.Build();
        var dir = TableDirectory.Parse(bytes);

        Assert.True(dir.TryGetRecord(OpenTypeTags.Head, out _));
        Assert.True(dir.TryGetRecord(OpenTypeTags.Hhea, out _));
        Assert.True(dir.TryGetRecord(OpenTypeTags.Hmtx, out _));
        Assert.True(dir.TryGetRecord(OpenTypeTags.Maxp, out _));
        Assert.True(dir.TryGetRecord(OpenTypeTags.Name, out _));
        Assert.True(dir.TryGetRecord(OpenTypeTags.Os2, out _));
        Assert.True(dir.TryGetRecord(OpenTypeTags.Post, out _));
        Assert.True(dir.TryGetRecord(OpenTypeTags.Cmap, out _));
        Assert.True(dir.TryGetRecord(OpenTypeTags.Loca, out _));
        Assert.True(dir.TryGetRecord(OpenTypeTags.Glyf, out _));
    }

    [Fact]
    public void GetTableBytes_returns_the_head_payload_when_passed_full_font()
    {
        var bytes = SyntheticFont.Build();
        var dir = TableDirectory.Parse(bytes);
        var headSlice = dir.GetTableBytes(OpenTypeTags.Head, bytes);
        Assert.Equal(54, headSlice.Length);
    }

    [Fact]
    public void GetTableBytes_throws_when_table_missing()
    {
        var bytes = SyntheticFont.Build();
        var dir = TableDirectory.Parse(bytes);
        // CFF is not present in the synthetic TTF
        Assert.Throws<InvalidDataException>(() => dir.GetTableBytes(OpenTypeTags.Cff, bytes));
    }

    [Fact]
    public void Parse_throws_on_unknown_sfnt_version()
    {
        var bytes = SyntheticFont.Build();
        bytes[0] = 0xFF; // corrupt magic
        Assert.Throws<InvalidDataException>(() => TableDirectory.Parse(bytes));
    }

    [Fact]
    public void Parse_throws_when_directory_truncated()
    {
        var bytes = SyntheticFont.Build();
        var truncated = bytes.AsSpan(0, 10).ToArray();
        Assert.Throws<InvalidDataException>(() => TableDirectory.Parse(truncated));
    }
}
