// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType;

/// <summary>
/// Post-Task-6 hardening: <see cref="OpenTypeFont.Parse"/> is the trust boundary for
/// downstream shaping / embedding. Cross-table inconsistencies must be rejected here so
/// later code never indexes <c>hmtx</c> / <c>loca</c> / <c>glyf</c> out of range.
/// </summary>
public sealed class OpenTypeFontHardeningTests
{
    [Fact]
    public void Parse_rejects_font_whose_cmap_maps_to_glyph_id_beyond_maxp_numGlyphs()
    {
        // Take the synthetic font and shrink maxp.numGlyphs to 2 so glyph id 2 (the cmap
        // mapping for 'B') is out of range. We also shrink hhea.numberOfHMetrics in lock-step
        // so the hmtx parser doesn't fail first — the test needs to reach the cross-table
        // validator, not earlier per-table consistency.
        var fontBytes = SyntheticFont.Build();
        ShrinkMaxpNumGlyphsTo(fontBytes, newNumGlyphs: 2);
        ShrinkHheaNumberOfHMetricsTo(fontBytes, newNumberOfHMetrics: 2);

        var ex = Assert.Throws<InvalidDataException>(() => OpenTypeFont.Parse(fontBytes));
        Assert.Contains("cmap group", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_accepts_font_when_every_cmap_glyph_id_is_within_numGlyphs()
    {
        // Sanity check: the unmodified synthetic font (numGlyphs=3, cmap maps to glyphs 1+2) parses cleanly.
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        Assert.Equal(SyntheticFont.NumGlyphs, font.Maxp.NumGlyphs);
        Assert.Equal((ushort)2, font.Cmap.GetGlyphId('B'));
    }

    private static void ShrinkMaxpNumGlyphsTo(byte[] fontBytes, ushort newNumGlyphs)
    {
        // numGlyphs sits at offset 4 of the maxp table (uint16 after the 4-byte version).
        OverwriteUInt16InTable(fontBytes, OpenTypeTags.Maxp, fieldOffsetInTable: 4, value: newNumGlyphs);
    }

    private static void ShrinkHheaNumberOfHMetricsTo(byte[] fontBytes, ushort newNumberOfHMetrics)
    {
        // numberOfHMetrics is the last uint16 of the 36-byte hhea table.
        OverwriteUInt16InTable(fontBytes, OpenTypeTags.Hhea, fieldOffsetInTable: 34, value: newNumberOfHMetrics);
    }

    /// <summary>Locate <paramref name="tag"/> via the SFNT directory and overwrite a uint16 field at <paramref name="fieldOffsetInTable"/>.</summary>
    private static void OverwriteUInt16InTable(byte[] fontBytes, uint tag, int fieldOffsetInTable, ushort value)
    {
        const int sfntHeaderSize = 12;
        const int recordSize = 16;
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(fontBytes.AsSpan(4, 2));
        for (var i = 0; i < numTables; i++)
        {
            var recordOffset = sfntHeaderSize + (i * recordSize);
            var foundTag = BinaryPrimitives.ReadUInt32BigEndian(fontBytes.AsSpan(recordOffset, 4));
            if (foundTag != tag)
            {
                continue;
            }
            var tableOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(fontBytes.AsSpan(recordOffset + 8, 4));
            BinaryPrimitives.WriteUInt16BigEndian(fontBytes.AsSpan(tableOffset + fieldOffsetInTable, 2), value);
            return;
        }
        throw new InvalidOperationException($"Table '{OpenTypeTags.ToAsciiString(tag)}' not found in synthetic font.");
    }
}
