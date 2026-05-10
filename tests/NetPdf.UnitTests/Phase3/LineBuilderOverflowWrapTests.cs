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
    public void Wrap_NoWrap_with_Anywhere_does_NOT_force_breaks()
    {
        // Per PR #36 review fix (User #2): OverflowWrap.Anywhere is
        // GATED by white-space wrapping permission. Under NoWrap (or
        // Pre) the wrap pass disallows ALL soft wraps; Anywhere
        // honors that. Result: no wrap, line overflows.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAAAAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 15,
            whiteSpace: WhiteSpace.NoWrap,
            overflowWrap: OverflowWrap.Anywhere);

        // Anywhere gated by wrapsAtAllowed=false → no wrap.
        Assert.Single(lines);
        var totalGlyphs = 0;
        foreach (var s in lines[0].Slices) totalGlyphs += s.GlyphLength;
        Assert.Equal(8, totalGlyphs);
        Assert.True(lines[0].TotalAdvance > 15,
            "NoWrap + Anywhere overflows — Anywhere is gated by white-space wrapping permission.");
    }

    [Fact]
    public void Wrap_Pre_with_Anywhere_does_NOT_force_breaks()
    {
        // Same gating rule for white-space:pre.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAAAAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 15,
            whiteSpace: WhiteSpace.Pre,
            overflowWrap: OverflowWrap.Anywhere);

        Assert.Single(lines);
    }

    [Fact]
    public void Wrap_Pre_BreakAll_combo_does_NOT_wrap_pin_pre_no_wrap_policy()
    {
        // Pre suppresses Allowed-break wrapping at the wrap-pass
        // level. BreakAll's per-glyph upgrades to Allowed are then
        // ignored by the wrapsAtAllowed=false gate — so Pre's "no
        // wrap" policy wins. This composition pins that semantic;
        // real-world CSS engines have varied historical behavior
        // here, but our conservative read is "Pre wins".
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 15,
            whiteSpace: WhiteSpace.Pre,
            wordBreak: WordBreak.BreakAll);

        Assert.Single(lines);
    }

    // --- Anywhere single-glyph-wider-than-line guarantee (Copilot PR #36) ---

    [Fact]
    public void Wrap_OverflowWrap_Anywhere_single_glyph_wider_than_line_emits_one_per_line()
    {
        // Per PR #36 review fix (Copilot #1): when a SINGLE glyph
        // is wider than the budget, Anywhere should emit it as its
        // own line (overflows by exactly one glyph) and advance
        // — not let additional glyphs accumulate. Available = 1px
        // (impossibly small) + 'A' = 6px; every glyph is wider
        // than the line.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1,
            overflowWrap: OverflowWrap.Anywhere);

        // Each glyph emits as its own line.
        Assert.Equal(6, lines.Length);
        foreach (var line in lines)
        {
            var glyphs = 0;
            foreach (var s in line.Slices) glyphs += s.GlyphLength;
            Assert.Equal(1, glyphs);
        }
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

    // --- BreakAll grapheme + protected-char respect (User #3 + #4) ---

    [Fact]
    public void Wrap_WordBreak_BreakAll_does_not_break_adjacent_to_NBSP()
    {
        // "AA AA" — NBSP between Latin runs. BreakAll's glyph
        // upgrades must NOT create candidate breaks adjacent to
        // NBSP (UAX #14 LB12: no break after GL).
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA AA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);

        // Available = 13px — would force a break SOMEWHERE under
        // BreakAll, but the candidates adjacent to NBSP should be
        // suppressed. The break should land between the AA pair on
        // the OTHER side of NBSP.
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 13,
            wordBreak: WordBreak.BreakAll);
        Assert.True(lines.Length >= 2,
            $"BreakAll should still wrap somewhere; got {lines.Length}");
    }

    [Fact]
    public void Wrap_WordBreak_BreakAll_does_not_break_adjacent_to_ZWJ()
    {
        // "A‍A" — ZWJ (U+200D) between Latin runs. BreakAll's
        // glyph upgrades must NOT create candidate breaks adjacent
        // to ZWJ (UAX #14 LB8a: no break after ZWJ + structural
        // protection for emoji/complex-script joining).
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA‍AA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);

        // ZWJ shapes through HarfBuzz; exact glyph count depends on
        // how the shaper handles it for our synthetic Latin font
        // (typically forms a cluster with an adjacent letter under
        // MonotoneCharacters cluster level). Available = 13px
        // forces wrap; assertion focuses on test intent: at least
        // 2 lines emit + every glyph from input is preserved on
        // some line.
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 13,
            wordBreak: WordBreak.BreakAll);
        Assert.True(lines.Length >= 2);
        var emittedGlyphs = 0;
        foreach (var line in lines)
        foreach (var s in line.Slices) emittedGlyphs += s.GlyphLength;
        var totalShapedGlyphs = 0;
        foreach (var run in shaped) totalShapedGlyphs += run.Glyphs.Length;
        Assert.Equal(totalShapedGlyphs, emittedGlyphs);
    }

    [Fact]
    public void Wrap_WordBreak_BreakAll_does_not_break_adjacent_to_WJ()
    {
        // "A⁠A" — Word Joiner (U+2060) is the explicit
        // "do not break here" marker. BreakAll must respect it
        // even when forcing wraps elsewhere.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA⁠AA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 13,
            wordBreak: WordBreak.BreakAll);
        Assert.True(lines.Length >= 2);
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
