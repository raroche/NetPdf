// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Fonts;
using NetPdf.Text.Fonts.OpenType;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Fonts;

/// <summary>
/// Post-Task-8 hardening: <see cref="GlyphSubsetPlan.Validate"/> is the trust boundary
/// for hand-constructed plans. Every structural invariant must throw at preflight time
/// rather than producing malformed PDF font data later.
/// </summary>
public sealed class GlyphSubsetPlanHardeningTests
{
    [Fact]
    public void Validate_passes_for_a_well_formed_built_plan()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { 1 });
        plan.Validate(font);
    }

    [Fact]
    public void Validate_throws_when_glyph_zero_not_at_new_id_zero()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = new GlyphSubsetPlan
        {
            OrderedOldGlyphIds = new[] { 1, 0 },
            OldToNew = new Dictionary<int, int> { { 1, 0 }, { 0, 1 } },
        };
        Assert.Throws<InvalidOperationException>(() => plan.Validate(font));
    }

    [Fact]
    public void Validate_throws_on_duplicate_old_glyph_id()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = new GlyphSubsetPlan
        {
            OrderedOldGlyphIds = new[] { 0, 1, 1 },
            OldToNew = new Dictionary<int, int> { { 0, 0 }, { 1, 2 } },
        };
        Assert.Throws<InvalidOperationException>(() => plan.Validate(font));
    }

    [Fact]
    public void Validate_throws_when_old_id_outside_font_universe()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build()); // numGlyphs = 3
        var plan = new GlyphSubsetPlan
        {
            OrderedOldGlyphIds = new[] { 0, 99 },
            OldToNew = new Dictionary<int, int> { { 0, 0 }, { 99, 1 } },
        };
        Assert.Throws<InvalidOperationException>(() => plan.Validate(font));
    }

    [Fact]
    public void Validate_throws_when_OldToNew_size_does_not_match_OrderedOldGlyphIds()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = new GlyphSubsetPlan
        {
            OrderedOldGlyphIds = new[] { 0, 1 },
            OldToNew = new Dictionary<int, int> { { 0, 0 } }, // missing 1 → 1
        };
        Assert.Throws<InvalidOperationException>(() => plan.Validate(font));
    }

    [Fact]
    public void Validate_throws_when_OldToNew_is_not_inverse_of_OrderedOldGlyphIds()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = new GlyphSubsetPlan
        {
            OrderedOldGlyphIds = new[] { 0, 1, 2 },
            OldToNew = new Dictionary<int, int> { { 0, 0 }, { 1, 2 }, { 2, 1 } }, // swapped
        };
        Assert.Throws<InvalidOperationException>(() => plan.Validate(font));
    }

    [Fact]
    public void Validate_throws_on_empty_plan()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = new GlyphSubsetPlan
        {
            OrderedOldGlyphIds = Array.Empty<int>(),
            OldToNew = new Dictionary<int, int>(),
        };
        Assert.Throws<InvalidOperationException>(() => plan.Validate(font));
    }

    [Fact]
    public void Cross_font_plan_is_rejected_via_old_id_range_check()
    {
        // Plan referencing glyph ids beyond the smaller font's universe — Validate catches it.
        var smallFont = OpenTypeFont.Parse(SyntheticFont.Build());           // numGlyphs = 3
        var largerFont = OpenTypeFont.Parse(SyntheticFontWithComposite.Build()); // numGlyphs = 4
        var plan = GlyphSubsetPlan.Build(largerFont, new HashSet<int> { 3 });
        Assert.Throws<InvalidOperationException>(() => plan.Validate(smallFont));
    }
}
