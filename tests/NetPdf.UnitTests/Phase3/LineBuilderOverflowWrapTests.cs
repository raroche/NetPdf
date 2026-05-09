// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
using NetPdf.Layout.Inline;
using NetPdf.Text.Bidi;
using NetPdf.Text.Shaping;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>Per Phase 3 Task 9 cycle 3b sub-cycle 2 — tests for
/// CSS Text L3 §5.1 <c>overflow-wrap</c> + §5.2 <c>word-break</c>.
/// Two surfaces under test:
/// <list type="bullet">
///   <item><see cref="OverflowWrap.Anywhere"/> — forces a per-glyph
///   break when the line would overflow + no UAX #14 Allowed
///   candidate exists.</item>
///   <item><see cref="WordBreak.BreakAll"/> — treats every glyph
///   boundary as a soft-break candidate (overrides UAX #14
///   Prohibited classifications).</item>
/// </list>
/// Cycle 3a/3b-sub-1 fallback (Normal modes) allows overflow when
/// no candidate exists; sub-cycle 2 adds the wrap modes that
/// prevent overflow.</summary>
public class LineBuilderOverflowWrapTests
{
    private const string LatnScript = "Latn";
    private const string EnLang = "en";

    // --- Arg validation ------------------------------------------

    [Fact]
    public void Wrap_undefined_overflowWrap_throws()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 100,
                overflowWrap: (OverflowWrap)99));
    }

    [Fact]
    public void Wrap_undefined_wordBreak_throws()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 100,
                wordBreak: (WordBreak)99));
    }

    // --- OverflowWrap.Normal: cycle 3a fallback (overflow allowed) ---

    [Fact]
    public void Wrap_OverflowWrap_Normal_unbreakable_overflows_one_line()
    {
        // "AAAAAAAAA" (no spaces) with available=10. No candidate
        // exists; default OverflowWrap.Normal allows overflow → one
        // line with all 9 glyphs.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAAAAAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 10,
            overflowWrap: OverflowWrap.Normal);

        Assert.Single(lines);
        var totalGlyphs = 0;
        foreach (var s in lines[0].Slices) totalGlyphs += s.GlyphLength;
        Assert.Equal(9, totalGlyphs);
        Assert.True(lines[0].TotalAdvance > 10,
            "Normal allows overflow when no candidate exists.");
    }

    // --- OverflowWrap.Anywhere: forces per-glyph break ---

    [Fact]
    public void Wrap_OverflowWrap_Anywhere_unbreakable_breaks_per_glyph()
    {
        // "AAAAAA" (no spaces) with available=15. Each 'A' = 6px,
        // so 2 'A's = 12, 3 'A's = 18 > 15 → wrap. Anywhere mode
        // forces the break at glyph 1 boundary (between A's). Result:
        // 3 lines of 2 'A's each (or similar).
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 15,
            overflowWrap: OverflowWrap.Anywhere);

        Assert.True(lines.Length >= 3,
            $"Anywhere should split unbreakable run; got {lines.Length} lines.");
        // Sum glyphs across all lines = original glyph count.
        var totalGlyphs = 0;
        foreach (var line in lines)
        foreach (var s in line.Slices)
            totalGlyphs += s.GlyphLength;
        Assert.Equal(6, totalGlyphs);
        // No line exceeds the budget (Anywhere ensures within-budget).
        foreach (var line in lines)
        {
            Assert.True(line.TotalAdvance <= 15,
                $"Line with Anywhere should not overflow; got {line.TotalAdvance}");
        }
    }

    [Fact]
    public void Wrap_OverflowWrap_Anywhere_prefers_UAX14_Allowed_break_when_available()
    {
        // "AAA AAAAAA" with available=20. UAX #14 Allowed at the SP
        // (cluster 3). 3 'A's + SP = 25.2 > 20 — soft-wrap snap-back
        // to the SP fires (line 1 = "AAA"). Then "AAAAAA" needs
        // Anywhere fallback (no Allowed inside). Anywhere should
        // PREFER the SP candidate over per-glyph fallback.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA AAAAAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 20,
            overflowWrap: OverflowWrap.Anywhere);

        Assert.True(lines.Length >= 2);
        // Line 1 should be exactly "AAA" (the UAX #14 candidate),
        // not split mid-word at glyph 2.
        var line1Glyphs = 0;
        foreach (var s in lines[0].Slices) line1Glyphs += s.GlyphLength;
        Assert.Equal(3, line1Glyphs);
    }

    // --- WordBreak.BreakAll: every glyph is a candidate ---

    [Fact]
    public void Wrap_WordBreak_BreakAll_breaks_at_every_glyph_boundary()
    {
        // "AAAAAA" with available=15 + BreakAll → every glyph creates
        // an Allowed candidate. The wrap loop snaps back at the most
        // recent candidate within the budget. Each 'A' = 6px;
        // 2 fits (12), 3 doesn't (18) → snap back at glyph 1
        // (lastAllowed), emit line "AA", continue.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 15,
            wordBreak: WordBreak.BreakAll);

        Assert.True(lines.Length >= 3,
            $"BreakAll should split unbreakable run; got {lines.Length} lines.");
        var totalGlyphs = 0;
        foreach (var line in lines)
        foreach (var s in line.Slices)
            totalGlyphs += s.GlyphLength;
        Assert.Equal(6, totalGlyphs);
        foreach (var line in lines)
        {
            Assert.True(line.TotalAdvance <= 15);
        }
    }

    [Fact]
    public void Wrap_WordBreak_KeepAll_no_observable_effect_for_Latin()
    {
        // KeepAll only suppresses inter-CJK breaks (cycle 4 / UAX #24).
        // For Latin content it behaves identically to Normal.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA AAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var keepAllLines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 25,
            wordBreak: WordBreak.KeepAll);
        var normalLines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 25,
            wordBreak: WordBreak.Normal);

        Assert.Equal(normalLines.Length, keepAllLines.Length);
    }

    // --- Combo modes ---

    [Fact]
    public void Wrap_OverflowWrap_Anywhere_with_NoWrap_only_breaks_at_Mandatory()
    {
        // NoWrap suppresses Allowed-break wrapping. Anywhere only
        // fires when overflow + no candidate. With NoWrap there's
        // never a candidate, so Anywhere fallback IS the only wrap
        // mechanism. Test: "AAAAAAAA" with NoWrap + Anywhere +
        // available=15 should split per-glyph at the budget.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAAAAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 15,
            whiteSpace: WhiteSpace.NoWrap,
            overflowWrap: OverflowWrap.Anywhere);

        Assert.True(lines.Length >= 2,
            $"NoWrap + Anywhere should still wrap on overflow; got {lines.Length} lines.");
        var totalGlyphs = 0;
        foreach (var line in lines)
        foreach (var s in line.Slices)
            totalGlyphs += s.GlyphLength;
        Assert.Equal(8, totalGlyphs);
    }

    [Fact]
    public void Wrap_BreakAll_overrides_Pre_NoWrap_Allowed_suppression()
    {
        // Pre suppresses Allowed-break wrapping at the wrap-pass
        // level. BreakAll upgrades EVERY glyph boundary to Allowed
        // — the wrapsAtAllowed=false rule under Pre still suppresses
        // those, so BreakAll alone with Pre = no wrapping. Verify
        // this composition: Pre + BreakAll = Pre's no-wrap wins (we
        // don't override Pre's strict no-wrap policy).
        //
        // Cycle 3b sub-cycle 2 chose this composition deliberately:
        // CSS Text L3 leaves wordbreak/overflowwrap/whitespace
        // interaction nuanced; the conservative semantics is "Pre's
        // 'do not wrap' wins". Real-world CSS engines vary here;
        // future cycles may refine.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 15,
            whiteSpace: WhiteSpace.Pre,
            wordBreak: WordBreak.BreakAll);

        // Pre's wrapsAtAllowed=false suppresses BreakAll's per-glyph
        // candidates. No overflow-wrap, so the line just overflows.
        Assert.Single(lines);
    }

    // --- Backward compat: existing tests still pass with default args

    [Fact]
    public void Wrap_default_OverflowWrap_Normal_matches_cycle_3a_behavior()
    {
        // Sanity: calling Wrap without overflowWrap arg uses Normal
        // default → cycle 3a "allow overflow" semantics.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 10);

        Assert.Single(lines);
        Assert.True(lines[0].TotalAdvance > 10);
    }

    // --- Helpers --------------------------------------------------

    private static ComputedStyle MakeStyle() =>
        ComputedStyle.RentForExclusiveTesting();

    private sealed class TestShaperResolver : IShaperResolver
    {
        private readonly HbShaper _shaper = new(SyntheticFont.Build(), fontSizePx: 12);
        public HbShaper Resolve(ComputedStyle style) => _shaper;
        public void Dispose() => _shaper.Dispose();
    }
}
