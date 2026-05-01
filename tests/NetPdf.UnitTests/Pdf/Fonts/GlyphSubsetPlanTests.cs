// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Fonts;
using NetPdf.Text.Fonts.OpenType;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Fonts;

public sealed class GlyphSubsetPlanTests
{
    [Fact]
    public void Plan_always_includes_glyph_zero_at_new_index_zero()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { 1 });

        Assert.Equal(0, plan.OrderedOldGlyphIds[0]);
        Assert.Equal(0, plan.OldToNew[0]);
    }

    [Fact]
    public void Empty_seed_still_yields_a_plan_with_just_notdef()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int>());

        Assert.Equal(1, plan.NumGlyphs);
        Assert.Equal(0, plan.OrderedOldGlyphIds[0]);
    }

    [Fact]
    public void Subset_of_one_simple_glyph_produces_two_glyph_plan()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { 1 });

        Assert.Equal(2, plan.NumGlyphs);
        Assert.Equal(new[] { 0, 1 }, plan.OrderedOldGlyphIds);
        Assert.Equal(0, plan.OldToNew[0]);
        Assert.Equal(1, plan.OldToNew[1]);
    }

    [Fact]
    public void Plan_orders_remaining_glyphs_by_ascending_original_id()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        // Seed in reverse insertion order — plan must still be [0, 1, 2].
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { 2, 1 });

        Assert.Equal(new[] { 0, 1, 2 }, plan.OrderedOldGlyphIds);
    }

    [Fact]
    public void Composite_chase_pulls_in_referenced_glyphs()
    {
        var font = OpenTypeFont.Parse(SyntheticFontWithComposite.Build());
        // Seed only the composite glyph 3. Its component (glyph 1) must be transparently added.
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { SyntheticFontWithComposite.CompositeGlyphIndex });

        Assert.Contains(SyntheticFontWithComposite.CompositeReferencedGlyph, plan.OrderedOldGlyphIds);
        Assert.Contains(SyntheticFontWithComposite.CompositeGlyphIndex, plan.OrderedOldGlyphIds);
        Assert.Equal(0, plan.OrderedOldGlyphIds[0]); // .notdef still first
    }

    [Fact]
    public void Build_throws_for_glyph_id_outside_font_universe()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GlyphSubsetPlan.Build(font, new HashSet<int> { 99 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GlyphSubsetPlan.Build(font, new HashSet<int> { -1 }));
    }

    [Fact]
    public void Build_throws_when_font_has_no_truetype_outlines()
    {
        var font = OpenTypeFont.Parse(NetPdf.UnitTests.Text.Fonts.OpenType.Cff.SyntheticOtf.Build());
        Assert.Throws<InvalidOperationException>(() =>
            GlyphSubsetPlan.Build(font, new HashSet<int> { 1 }));
    }

    [Fact]
    public void Plan_is_deterministic_across_repeated_builds()
    {
        var font = OpenTypeFont.Parse(SyntheticFontWithComposite.Build());
        var seed = new HashSet<int> { 3, 1 };
        var a = GlyphSubsetPlan.Build(font, seed);
        var b = GlyphSubsetPlan.Build(font, seed);

        Assert.Equal(a.OrderedOldGlyphIds, b.OrderedOldGlyphIds);
        foreach (var (k, v) in a.OldToNew)
        {
            Assert.Equal(v, b.OldToNew[k]);
        }
    }
}
