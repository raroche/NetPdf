// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Pdf.Fonts;
using NetPdf.Text.Fonts.OpenType;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Fonts;

/// <summary>
/// Integration tests for <see cref="ToUnicodeCMap.FromSubset"/> — drives the full
/// pipeline (<see cref="OpenTypeFont"/> parse → <see cref="GlyphSubsetPlan"/> build →
/// <see cref="ToUnicodeCMap"/> emission) end-to-end against the synthetic font.
/// </summary>
public sealed class ToUnicodeCMapIntegrationTests
{
    [Fact]
    public void FromSubset_maps_each_subset_glyph_to_its_source_codepoint()
    {
        // SyntheticFont: cmap maps 'A' (U+0041) → glyph 1 and 'B' (U+0042) → glyph 2.
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { 1, 2 });
        var cmap = ToUnicodeCMap.FromSubset(font, plan);

        Assert.Equal("A", cmap.SubsetGlyphIdToText[plan.OldToNew[1]]);
        Assert.Equal("B", cmap.SubsetGlyphIdToText[plan.OldToNew[2]]);
    }

    [Fact]
    public void FromSubset_does_not_map_glyph_zero_notdef()
    {
        // .notdef has no Unicode mapping in the cmap; it's structurally in the subset
        // (always at new id 0) but should not appear in the ToUnicode map.
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { 1 });
        var cmap = ToUnicodeCMap.FromSubset(font, plan);

        Assert.False(cmap.SubsetGlyphIdToText.ContainsKey(0));
    }

    [Fact]
    public void FromSubset_skips_glyphs_outside_the_subset()
    {
        // Subset only includes glyph 1; cmap walk encounters glyph 2 but must skip it.
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { 1 });
        var cmap = ToUnicodeCMap.FromSubset(font, plan);

        Assert.Single(cmap.SubsetGlyphIdToText);
        Assert.True(cmap.SubsetGlyphIdToText.ContainsKey(plan.OldToNew[1]));
    }

    [Fact]
    public void Emitted_cmap_for_synthetic_font_round_trips_back_to_letters_A_and_B()
    {
        // Full integration: parse the emitted CMap text and confirm the bfchar block
        // declares the expected source-id → target-codepoint pairs.
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { 1, 2 });
        var cmap = ToUnicodeCMap.FromSubset(font, plan);
        var text = Encoding.ASCII.GetString(cmap.Emit());

        // Subset glyph ids 1 and 2 (since plan order is [0, 1, 2] and OldToNew is identity here).
        Assert.Contains("2 beginbfchar", text);
        Assert.Contains("<0001> <0041>", text); // glyph 1 → 'A'
        Assert.Contains("<0002> <0042>", text); // glyph 2 → 'B'
        Assert.Contains("endbfchar", text);
    }

    [Fact]
    public void FromSubset_is_deterministic_for_byte_equal_inputs()
    {
        var fontBytes = SyntheticFont.Build();
        var fontA = OpenTypeFont.Parse(fontBytes);
        var fontB = OpenTypeFont.Parse(fontBytes);
        var planA = GlyphSubsetPlan.Build(fontA, new HashSet<int> { 1, 2 });
        var planB = GlyphSubsetPlan.Build(fontB, new HashSet<int> { 1, 2 });

        var bytesA = ToUnicodeCMap.FromSubset(fontA, planA).Emit();
        var bytesB = ToUnicodeCMap.FromSubset(fontB, planB).Emit();

        Assert.Equal(bytesA, bytesB);
    }

    [Fact]
    public void FromSubset_validates_plan_against_font()
    {
        // Plan built from a different (larger) font but applied to a smaller font — the
        // shared GlyphSubsetPlan.Validate(font) check rejects.
        var smallFont = OpenTypeFont.Parse(SyntheticFont.Build());
        var largerFont = OpenTypeFont.Parse(SyntheticFontWithComposite.Build());
        var planFromLargerFont = GlyphSubsetPlan.Build(largerFont, new HashSet<int> { 3 });

        Assert.Throws<InvalidOperationException>(() =>
            ToUnicodeCMap.FromSubset(smallFont, planFromLargerFont));
    }
}
