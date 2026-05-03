// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Fonts;
using NetPdf.Pdf.Objects;
using NetPdf.Text.Fonts.OpenType;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Fonts;

/// <summary>
/// Post-Task-10 hardening: <see cref="EmbeddedTtfFont.Build"/> is the trust boundary
/// where structurally-bad inputs and license-restricted fonts must reject before any
/// byte production. Also verifies the /W advance-width fix and PostScript-name
/// sanitization for international family names.
/// </summary>
public sealed class EmbeddedTtfFontHardeningTests
{
    // ───── /W width bug fix (P1 #1) ───────────────────────────────────────────

    [Fact]
    public void Widths_array_preserves_advance_above_32767()
    {
        // Mutate glyph 1's hmtx advance to 60000 — wider than int16.MaxValue.
        // Without the (short) cast fix this would wrap to a negative scaled width.
        var fontBytes = SyntheticFont.Build();
        FontByteMutator.SetHmtxAdvance(fontBytes, glyphIndex: 1, newAdvance: 60000);
        var embedded = BuildEmbeddedFromBytes(fontBytes, new HashSet<int> { 1 });

        var w = (PdfArray)embedded.CidFontDictionary.Get(PdfNames.W)!;
        var perGlyph = (PdfArray)w[1];
        // SyntheticFont unitsPerEm = 1000 → glyph space units = 60000 (1:1).
        // New subset glyph 1 corresponds to original glyph 1.
        var glyph1Width = ((PdfInteger)perGlyph[1]).Value;
        Assert.Equal(60000L, glyph1Width);
    }

    // ───── fsType embedding policy (P1 #2) ────────────────────────────────────

    [Fact]
    public void Build_rejects_font_with_restricted_license_fsType_bit()
    {
        var fontBytes = SyntheticFont.Build();
        FontByteMutator.SetFsType(fontBytes, 0x0002); // restricted-license
        var ex = Assert.Throws<InvalidOperationException>(() => BuildEmbeddedFromBytes(fontBytes, new HashSet<int> { 1 }));
        Assert.Contains("restricted-license", ex.Message);
    }

    [Fact]
    public void Build_rejects_font_with_no_subsetting_fsType_bit()
    {
        var fontBytes = SyntheticFont.Build();
        FontByteMutator.SetFsType(fontBytes, 0x0100); // no-subsetting
        var ex = Assert.Throws<InvalidOperationException>(() => BuildEmbeddedFromBytes(fontBytes, new HashSet<int> { 1 }));
        Assert.Contains("no-subsetting", ex.Message);
    }

    [Fact]
    public void Build_rejects_font_with_bitmap_only_fsType_bit()
    {
        var fontBytes = SyntheticFont.Build();
        FontByteMutator.SetFsType(fontBytes, 0x0200); // bitmap-only
        var ex = Assert.Throws<InvalidOperationException>(() => BuildEmbeddedFromBytes(fontBytes, new HashSet<int> { 1 }));
        Assert.Contains("bitmap-only", ex.Message);
    }

    [Fact]
    public void Build_accepts_installable_or_preview_print_or_editable_fsType()
    {
        // fsType = 0 (installable, the default), 0x0004 (preview & print), 0x0008 (editable)
        // — none are restricted.
        foreach (ushort allowedFsType in new ushort[] { 0x0000, 0x0004, 0x0008 })
        {
            var fontBytes = SyntheticFont.Build();
            FontByteMutator.SetFsType(fontBytes, allowedFsType);
            var embedded = BuildEmbeddedFromBytes(fontBytes, new HashSet<int> { 1 });
            Assert.NotNull(embedded);
        }
    }

    // ───── Trust-boundary preflight (P1 #3) ───────────────────────────────────

    [Fact]
    public void Build_rejects_hand_built_subset_with_inconsistent_hmtx_length()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { 1, 2 }); // 3 glyphs in plan
        var goodSubset = TtfSubsetter.Subset(font, plan);

        // Construct a malformed subset where hmtx length doesn't match the plan's glyph count.
        var badSubset = new TtfSubsetResult
        {
            Plan = plan,
            SubsetBaseFontName = goodSubset.SubsetBaseFontName,
            HeadBytes = goodSubset.HeadBytes,
            HheaBytes = goodSubset.HheaBytes,
            MaxpBytes = goodSubset.MaxpBytes,
            HmtxBytes = goodSubset.HmtxBytes.Slice(0, 4), // only 1 glyph's worth of metrics
            LocaBytes = goodSubset.LocaBytes,
            GlyfBytes = goodSubset.GlyfBytes,
        };
        var toUnicode = ToUnicodeCMap.FromSubset(font, plan);

        var ex = Assert.Throws<InvalidOperationException>(() => EmbeddedTtfFont.Build(font, badSubset, toUnicode));
        Assert.Contains("hmtx", ex.Message);
    }

    [Fact]
    public void Build_rejects_ToUnicode_map_containing_subset_glyph_ids_outside_range()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { 1 }); // subset has 2 glyphs (.notdef + 1)
        var subset = TtfSubsetter.Subset(font, plan);

        // Hand-built ToUnicode that maps subset glyph id 99 — outside [0, 2).
        var badMap = new ToUnicodeCMap
        {
            SubsetGlyphIdToText = new Dictionary<int, string> { { 99, "X" } },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => EmbeddedTtfFont.Build(font, subset, badMap));
        Assert.Contains("ToUnicode", ex.Message);
    }

    [Fact]
    public void Build_rejects_cross_font_subset_via_plan_validation()
    {
        // Build a subset against a larger font, hand it to Build with a smaller font.
        // The plan.Validate(font) call inside the preflight catches this.
        var smallFont = OpenTypeFont.Parse(SyntheticFont.Build());
        var largerFont = OpenTypeFont.Parse(SyntheticFontWithComposite.Build());
        var planFromLargerFont = GlyphSubsetPlan.Build(largerFont, new HashSet<int> { 3 });
        var subsetFromLargerFont = TtfSubsetter.Subset(largerFont, planFromLargerFont);
        var toUnicode = ToUnicodeCMap.FromSubset(largerFont, planFromLargerFont);

        Assert.Throws<InvalidOperationException>(() => EmbeddedTtfFont.Build(smallFont, subsetFromLargerFont, toUnicode));
    }

    // ───── PostScript-name sanitization (P2 #4) ───────────────────────────────

    [Fact]
    public void BaseFont_falls_back_to_sanitized_FamilyName_when_PostScriptName_missing()
    {
        // Strip the synthetic name table down to a Unicode-only family name, no PostScript record.
        var fontBytes = SyntheticFont.Build();
        var withCJK = FontByteMutator.ReplaceNameTableWithFamilyOnly(fontBytes, "源ノ角ゴシック");
        var embedded = BuildEmbeddedFromBytes(withCJK, new HashSet<int> { 1 });

        // Sanitizer drops every non-ASCII char and falls back to "Font" + 8-hex when
        // nothing alphabetic survives. Resulting BaseFont must still be ASCII-only.
        foreach (var c in embedded.SubsetBaseFontName)
        {
            Assert.True(c < 0x80, $"BaseFont contains non-ASCII char 0x{(int)c:X4}");
        }
        Assert.Contains("Font", embedded.SubsetBaseFontName);
    }

    [Fact]
    public void PostScriptName_Sanitize_keeps_letters_digits_dashes_plus_underscore()
    {
        Assert.Equal("Helvetica-Bold", PostScriptName.Sanitize("Helvetica-Bold"));
        Assert.Equal("Times2024_v1", PostScriptName.Sanitize("Times2024_v1"));
        Assert.Equal("MyFontA+B", PostScriptName.Sanitize("MyFontA+B"));
    }

    [Fact]
    public void PostScriptName_Sanitize_drops_whitespace_and_postscript_specials()
    {
        Assert.Equal("HelveticaBold", PostScriptName.Sanitize("Helvetica Bold"));
        Assert.Equal("FontName", PostScriptName.Sanitize("Font(Name)"));
        Assert.Equal("XY", PostScriptName.Sanitize("X/Y"));
    }

    [Fact]
    public void PostScriptName_Sanitize_falls_back_to_hash_when_no_letters_survive()
    {
        var result = PostScriptName.Sanitize("源ノ角ゴシック");
        Assert.StartsWith("Font", result);
        Assert.Equal("Font".Length + 8, result.Length); // "Font" + 8 hex chars
        // Determinism — same input → same output.
        Assert.Equal(result, PostScriptName.Sanitize("源ノ角ゴシック"));
        // Different inputs → different outputs.
        Assert.NotEqual(result, PostScriptName.Sanitize("Другой шрифт"));
    }

    [Fact]
    public void PostScriptName_Sanitize_truncates_at_63_chars()
    {
        var longName = new string('A', 100);
        var result = PostScriptName.Sanitize(longName);
        Assert.Equal(63, result.Length);
    }

    // ───── Hinting-table pass-through (P2 #5) ─────────────────────────────────

    [Fact]
    public void Embedded_SFNT_includes_fpgm_when_source_font_provides_one()
    {
        var fontBytes = SyntheticFont.Build();
        var fpgmBytes = new byte[] { 0xB0, 0x05, 0x4B, 0xB0, 0x09, 0x50 }; // arbitrary opaque hinting bytecode
        var withFpgm = FontByteMutator.AddFpgmTable(fontBytes, fpgmBytes);
        var embedded = BuildEmbeddedFromBytes(withFpgm, new HashSet<int> { 1 });

        var directory = TableDirectory.Parse(embedded.FontFile2Stream.Data);
        Assert.True(directory.TryGetRecord(OpenTypeTags.Fpgm, out var fpgmRecord));

        var emittedFpgm = directory.GetTableBytes(OpenTypeTags.Fpgm, embedded.FontFile2Stream.Data);
        Assert.Equal(fpgmBytes, emittedFpgm.ToArray());
    }

    [Fact]
    public void Embedded_SFNT_omits_hinting_tables_when_source_font_lacks_them()
    {
        // Synthetic font has no fpgm/cvt/prep/gasp; embedded SFNT should not invent them.
        var embedded = BuildEmbeddedFromBytes(SyntheticFont.Build(), new HashSet<int> { 1 });
        var directory = TableDirectory.Parse(embedded.FontFile2Stream.Data);
        Assert.False(directory.TryGetRecord(OpenTypeTags.Fpgm, out _));
        Assert.False(directory.TryGetRecord(OpenTypeTags.Cvt, out _));
        Assert.False(directory.TryGetRecord(OpenTypeTags.Prep, out _));
        Assert.False(directory.TryGetRecord(OpenTypeTags.Gasp, out _));
    }

    // ───── helper ─────────────────────────────────────────────────────────────

    private static EmbeddedFont BuildEmbeddedFromBytes(byte[] fontBytes, HashSet<int> seedGlyphIds)
    {
        var font = OpenTypeFont.Parse(fontBytes);
        var plan = GlyphSubsetPlan.Build(font, seedGlyphIds);
        var subset = TtfSubsetter.Subset(font, plan);
        var toUnicode = ToUnicodeCMap.FromSubset(font, plan);
        return EmbeddedTtfFont.Build(font, subset, toUnicode);
    }
}
