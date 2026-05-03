// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType;

/// <summary>
/// Integration tests: drive the entire OpenType parser pipeline (SFNT directory →
/// 10 individual table parsers → top-level <see cref="OpenTypeFont"/>) against a
/// synthetic-but-valid TTF byte stream and assert cross-table consistency.
/// </summary>
public sealed class OpenTypeFontTests
{
    [Fact]
    public void Parse_synthetic_font_returns_all_required_tables()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());

        Assert.NotNull(font.Head);
        Assert.NotNull(font.Hhea);
        Assert.NotNull(font.Maxp);
        Assert.NotNull(font.Os2);
        Assert.NotNull(font.Post);
        Assert.NotNull(font.Name);
        Assert.NotNull(font.Hmtx);
        Assert.NotNull(font.Cmap);
        Assert.NotNull(font.Loca);
        Assert.NotNull(font.Glyf);
        Assert.True(font.HasTrueTypeOutlines);
    }

    [Fact]
    public void Parse_yields_consistent_glyph_counts_across_tables()
    {
        // maxp.numGlyphs governs the universe; loca + hmtx + glyf must agree.
        var font = OpenTypeFont.Parse(SyntheticFont.Build());

        Assert.Equal(SyntheticFont.NumGlyphs, font.Maxp.NumGlyphs);
        Assert.Equal(SyntheticFont.NumGlyphs, font.Hmtx.AdvanceWidths.Length);
        Assert.Equal(SyntheticFont.NumGlyphs, font.Hmtx.LeftSideBearings.Length);
        Assert.Equal(SyntheticFont.NumGlyphs, font.Loca!.NumGlyphs);
    }

    [Fact]
    public void Parse_resolves_postscript_name_from_name_table()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        Assert.Equal("Synth-Test", font.Name.PostScriptName);
    }

    [Fact]
    public void Cmap_to_loca_to_glyf_round_trip_yields_glyph_bytes_for_letter_A()
    {
        // The end-to-end story Phase 1 cares about: Unicode codepoint 'A' must resolve
        // through cmap to glyph 1 → loca offsets to a non-empty glyf slice.
        var font = OpenTypeFont.Parse(SyntheticFont.Build());

        var glyphId = font.Cmap.GetGlyphId('A');
        Assert.Equal((ushort)1, glyphId);

        var glyphLength = font.Loca!.GetGlyphLength(glyphId);
        Assert.Equal(36u, glyphLength);

        var glyphBytes = font.Glyf!.GetGlyphBytes(glyphId);
        Assert.Equal(36, glyphBytes.Length);

        // Hmtx advance width for that glyph also reachable.
        Assert.Equal((ushort)500, font.Hmtx.AdvanceWidths[glyphId]);
    }

    [Fact]
    public void Parse_decodes_units_per_em_consistent_with_typo_metrics()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        Assert.Equal(SyntheticFont.UnitsPerEm, font.Head.UnitsPerEm);
        // Hhea + OS/2 typo metrics agree (we built them that way; this enforces the invariant).
        Assert.Equal(font.Hhea.Ascender, font.Os2.STypoAscender);
        Assert.Equal(font.Hhea.Descender, font.Os2.STypoDescender);
        Assert.Equal(font.Hhea.LineGap, font.Os2.STypoLineGap);
    }

    [Fact]
    public void Parse_throws_on_empty_font_bytes()
    {
        Assert.Throws<ArgumentException>(() => OpenTypeFont.Parse(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Parse_is_deterministic_across_repeated_calls()
    {
        // The parser builds new collections on each call but the contents must be value-equal
        // for byte-equal input. This is a property test for the determinism guarantee.
        var bytes = SyntheticFont.Build();
        var a = OpenTypeFont.Parse(bytes);
        var b = OpenTypeFont.Parse(bytes);

        Assert.Equal(a.Maxp.NumGlyphs, b.Maxp.NumGlyphs);
        Assert.Equal(a.Hmtx.AdvanceWidths, b.Hmtx.AdvanceWidths);
        Assert.Equal(a.Loca!.Offsets, b.Loca!.Offsets);
        Assert.Equal(a.Cmap.GetGlyphId('A'), b.Cmap.GetGlyphId('A'));
        Assert.Equal(a.Name.PostScriptName, b.Name.PostScriptName);
    }
}
