// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Pdf.Fonts;
using NetPdf.Text.Fonts.OpenType;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Fonts;

/// <summary>
/// Per-class unit + dual-layer integration tests for <see cref="TtfSubsetter"/>. The
/// integration layer re-parses the emitted subset bytes through the OpenType table
/// parsers from Task 6 (<see cref="HeadTable"/>, <see cref="MaxpTable"/>,
/// <see cref="HmtxTable"/>, <see cref="LocaTable"/>) — that's the cross-component
/// composition the dual-layer rule requires.
/// </summary>
public sealed class TtfSubsetterTests
{
    // ───── Unit: per-table emission ───────────────────────────────────────────

    [Fact]
    public void Subset_emits_correct_glyph_count_in_maxp()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { 1 });
        var result = TtfSubsetter.Subset(font, plan);

        var newMaxp = MaxpTable.Parse(result.MaxpBytes);
        Assert.Equal((ushort)2, newMaxp.NumGlyphs);
    }

    [Fact]
    public void Subset_emits_one_long_metric_per_subset_glyph()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { 1 });
        var result = TtfSubsetter.Subset(font, plan);

        var newHhea = HheaTable.Parse(result.HheaBytes);
        Assert.Equal((ushort)2, newHhea.NumberOfHMetrics);

        var newHmtx = HmtxTable.Parse(result.HmtxBytes, newHhea.NumberOfHMetrics, plan.NumGlyphs.Equals(0) ? (ushort)0 : (ushort)plan.NumGlyphs);
        Assert.Equal(plan.NumGlyphs, newHmtx.AdvanceWidths.Length);
    }

    [Fact]
    public void Subset_hmtx_carries_per_glyph_advance_from_source()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { 1 });
        var result = TtfSubsetter.Subset(font, plan);

        // Source glyph 1 had advance 500. After subsetting, new glyph 1 (mapped from old 1) keeps advance 500.
        var newHmtx = HmtxTable.Parse(result.HmtxBytes, numberOfHMetrics: 2, numGlyphs: 2);
        Assert.Equal(font.Hmtx.AdvanceWidths[0], newHmtx.AdvanceWidths[0]); // .notdef advance preserved
        Assert.Equal(font.Hmtx.AdvanceWidths[1], newHmtx.AdvanceWidths[1]); // glyph 1's advance preserved
    }

    [Fact]
    public void Subset_loca_offsets_are_non_decreasing()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { 1 });
        var result = TtfSubsetter.Subset(font, plan);

        var newHead = HeadTable.Parse(result.HeadBytes);
        var newLoca = LocaTable.Parse(result.LocaBytes, (ushort)plan.NumGlyphs, newHead.IndexToLocFormat);

        for (var i = 1; i < newLoca.Offsets.Length; i++)
        {
            Assert.True(newLoca.Offsets[i] >= newLoca.Offsets[i - 1]);
        }
        Assert.Equal((uint)result.GlyfBytes.Length, newLoca.Offsets[^1]);
    }

    [Fact]
    public void Subset_BaseFont_name_carries_six_letter_prefix_and_separator()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { 1 });
        var result = TtfSubsetter.Subset(font, plan);

        Assert.Equal('+', result.SubsetBaseFontName[6]);
        Assert.True(result.SubsetBaseFontName.Length > 7);
        for (var i = 0; i < 6; i++)
        {
            Assert.InRange(result.SubsetBaseFontName[i], 'A', 'Z');
        }
    }

    [Fact]
    public void Subset_head_zeroes_checkSumAdjustment_for_envelope_recompute()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { 1 });
        var result = TtfSubsetter.Subset(font, plan);

        // checkSumAdjustment is at offset 8 of head. Phase 1 Task 10 (the SFNT envelope
        // assembler) recomputes it after the final layout, so the subsetter zeroes it.
        var checkSum = BinaryPrimitives.ReadUInt32BigEndian(result.HeadBytes.AsSpan(8, 4));
        Assert.Equal(0u, checkSum);
    }

    [Fact]
    public void Subset_throws_for_non_ttf_font()
    {
        var font = OpenTypeFont.Parse(NetPdf.UnitTests.Text.Fonts.OpenType.Cff.SyntheticOtf.Build());
        var plan = new GlyphSubsetPlan
        {
            OrderedOldGlyphIds = new[] { 0, 1 },
            OldToNew = new Dictionary<int, int> { { 0, 0 }, { 1, 1 } },
        };
        Assert.Throws<InvalidOperationException>(() => TtfSubsetter.Subset(font, plan));
    }

    // ───── Composite: rewriting glyphIndex ────────────────────────────────────

    [Fact]
    public void Composite_glyph_component_glyphIndex_is_rewritten_to_new_id()
    {
        var font = OpenTypeFont.Parse(SyntheticFontWithComposite.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { SyntheticFontWithComposite.CompositeGlyphIndex });
        var result = TtfSubsetter.Subset(font, plan);

        // Look up the new id of the originally-composite glyph and find its bytes in the subset glyf.
        var newCompositeId = plan.OldToNew[SyntheticFontWithComposite.CompositeGlyphIndex];
        var newHead = HeadTable.Parse(result.HeadBytes);
        var newLoca = LocaTable.Parse(result.LocaBytes, (ushort)plan.NumGlyphs, newHead.IndexToLocFormat);

        var compositeStart = (int)newLoca.Offsets[newCompositeId];
        var compositeEnd = (int)newLoca.Offsets[newCompositeId + 1];
        var compositeBytes = result.GlyfBytes.AsSpan(compositeStart, compositeEnd - compositeStart);

        // Confirm it's still a composite (numberOfContours < 0) and read the rewritten glyphIndex.
        var numContours = BinaryPrimitives.ReadInt16BigEndian(compositeBytes[0..2]);
        Assert.True(numContours < 0);
        var rewrittenGlyphIndex = BinaryPrimitives.ReadUInt16BigEndian(compositeBytes[12..14]);
        // Original component was glyph 1 — must now point at its new subset id.
        Assert.Equal(plan.OldToNew[SyntheticFontWithComposite.CompositeReferencedGlyph], rewrittenGlyphIndex);
    }

    // ───── Determinism property ───────────────────────────────────────────────

    [Fact]
    public void Subset_is_deterministic_for_byte_equal_inputs()
    {
        var fontBytes = SyntheticFont.Build();
        var fontA = OpenTypeFont.Parse(fontBytes);
        var fontB = OpenTypeFont.Parse(fontBytes);
        var planA = GlyphSubsetPlan.Build(fontA, new HashSet<int> { 1 });
        var planB = GlyphSubsetPlan.Build(fontB, new HashSet<int> { 1 });
        var resultA = TtfSubsetter.Subset(fontA, planA);
        var resultB = TtfSubsetter.Subset(fontB, planB);

        Assert.Equal(resultA.GlyfBytes, resultB.GlyfBytes);
        Assert.Equal(resultA.LocaBytes, resultB.LocaBytes);
        Assert.Equal(resultA.HmtxBytes, resultB.HmtxBytes);
        Assert.Equal(resultA.HeadBytes, resultB.HeadBytes);
        Assert.Equal(resultA.HheaBytes, resultB.HheaBytes);
        Assert.Equal(resultA.MaxpBytes, resultB.MaxpBytes);
        Assert.Equal(resultA.SubsetBaseFontName, resultB.SubsetBaseFontName);
    }

    // ───── Integration: subset bytes parse back through OpenType parsers ──────

    [Fact]
    public void Integration_subset_tables_parse_back_through_OpenType_parsers()
    {
        var font = OpenTypeFont.Parse(SyntheticFontWithComposite.Build());
        var plan = GlyphSubsetPlan.Build(font, new HashSet<int> { 1, 3 });
        var result = TtfSubsetter.Subset(font, plan);

        // head re-parses with valid magic + valid indexToLocFormat.
        var newHead = HeadTable.Parse(result.HeadBytes);
        Assert.True(newHead.IndexToLocFormat is 0 or 1);

        // hhea re-parses; numberOfHMetrics matches subset.
        var newHhea = HheaTable.Parse(result.HheaBytes);
        Assert.Equal(plan.NumGlyphs, newHhea.NumberOfHMetrics);

        // maxp re-parses.
        var newMaxp = MaxpTable.Parse(result.MaxpBytes);
        Assert.Equal(plan.NumGlyphs, newMaxp.NumGlyphs);

        // hmtx re-parses against the new (numberOfHMetrics, numGlyphs).
        var newHmtx = HmtxTable.Parse(result.HmtxBytes, newHhea.NumberOfHMetrics, newMaxp.NumGlyphs);
        Assert.Equal(plan.NumGlyphs, newHmtx.AdvanceWidths.Length);

        // loca re-parses — offsets non-decreasing, last offset matches glyf length.
        var newLoca = LocaTable.Parse(result.LocaBytes, newMaxp.NumGlyphs, newHead.IndexToLocFormat);
        Assert.Equal(plan.NumGlyphs, newLoca.NumGlyphs);
        Assert.Equal((uint)result.GlyfBytes.Length, newLoca.Offsets[^1]);
    }
}
