// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType.Cff;

/// <summary>
/// Integration tests for the CFF path through <see cref="OpenTypeFont.Parse"/>: drives
/// the entire SFNT-directory → 8-table-parser → CFF-orchestrator pipeline against a
/// synthetic OTF/CFF byte stream and asserts cross-table consistency.
/// </summary>
public sealed class OpenTypeFontCffTests
{
    [Fact]
    public void Parse_synthetic_otf_marks_font_as_cff()
    {
        var font = OpenTypeFont.Parse(SyntheticOtf.Build());

        Assert.True(font.Directory.IsCff);
        Assert.False(font.Directory.IsTrueType);
        Assert.True(font.HasCffOutlines);
        Assert.False(font.HasTrueTypeOutlines);
    }

    [Fact]
    public void Parse_synthetic_otf_returns_cff_table_with_expected_glyph_count()
    {
        var font = OpenTypeFont.Parse(SyntheticOtf.Build());

        Assert.NotNull(font.Cff);
        Assert.Equal(SyntheticCff.NumGlyphs, font.Cff.NumGlyphs);
        Assert.Equal(SyntheticCff.FontName, font.Cff.FontName);
    }

    [Fact]
    public void Parse_synthetic_otf_keeps_loca_and_glyf_null_for_cff_flavor()
    {
        var font = OpenTypeFont.Parse(SyntheticOtf.Build());
        Assert.Null(font.Loca);
        Assert.Null(font.Glyf);
    }

    [Fact]
    public void Parse_resolves_charstrings_through_OpenTypeFont_Cff()
    {
        var font = OpenTypeFont.Parse(SyntheticOtf.Build());
        Assert.NotNull(font.Cff);

        for (var i = 0; i < font.Cff.NumGlyphs; i++)
        {
            var charString = font.Cff.GetCharStringBytes(i);
            Assert.False(charString.IsEmpty);
            Assert.Equal((byte)0x0E, charString[0]); // endchar
        }
    }

    [Fact]
    public void Parse_otf_runs_same_cross_table_validation_as_ttf_path()
    {
        // Cross-table check (ValidateCmapAgainstMaxp) runs on every parse path. The cmap on
        // our synthetic OTF maps 'A' → 1 and 'B' → 2; maxp.numGlyphs is 3. All consistent.
        var font = OpenTypeFont.Parse(SyntheticOtf.Build());
        Assert.Equal((ushort)1, font.Cmap.GetGlyphId('A'));
        Assert.Equal((ushort)2, font.Cmap.GetGlyphId('B'));
        Assert.True(font.Cmap.GetGlyphId('B') < font.Maxp.NumGlyphs);
    }

    [Fact]
    public void Parse_otf_yields_consistent_glyph_count_across_maxp_hmtx_and_cff()
    {
        var font = OpenTypeFont.Parse(SyntheticOtf.Build());
        Assert.Equal(SyntheticCff.NumGlyphs, font.Maxp.NumGlyphs);
        Assert.Equal(SyntheticCff.NumGlyphs, font.Hmtx.AdvanceWidths.Length);
        Assert.NotNull(font.Cff);
        Assert.Equal(SyntheticCff.NumGlyphs, font.Cff.NumGlyphs);
    }
}
