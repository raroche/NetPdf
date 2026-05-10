// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Inline;
using NetPdf.Text.Shaping;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>Per Phase 3 Task 10 cycle 3c — tests for per-glyph
/// WhiteSpace honoring (mixed-mode <c>NoWrap</c>-inside-<c>Normal</c>
/// finally works without throwing).
///
/// <para>Cycle 3b's `LayoutPerRun` threw `NotSupportedException`
/// for any policy mismatch; cycle 3c relaxes this for WhiteSpace-
/// only mismatches by building a per-source-run WhiteSpace array
/// + plumbing to <see cref="LineBuilder.Wrap"/>'s new
/// <c>whiteSpacePerRun</c> parameter. Glyphs in NoWrap/Pre runs
/// get their Allowed-break opportunities suppressed.</para>
///
/// <para><b>Post-PR-#42 review hardening (cycle 3c review).</b> Tests
/// were strengthened from "did not throw" to exact line/slice/glyph
/// assertions per Rec #5 + Copilot #5. Also narrowed the accepted
/// WhiteSpace mismatch matrix to {Normal, NoWrap}-only per Recs
/// #1+#3 — Pre/PreWrap/PreLine/BreakSpaces mixed with collapse modes
/// now throw <c>NotSupportedException</c> until cycle 3d ships
/// per-source-run preprocessing.</para>
///
/// <para><b>Synthetic font advance reference.</b> The
/// <see cref="SyntheticFont"/> at fontSizePx=12 + UPEM=1000 yields:
/// <list type="bullet">
///   <item>'A' / 'B' → glyph 1 / 2, advance 500/1000 × 12 = 6.0 px.</item>
///   <item>Anything else (incl. ' ') → glyph 0 (.notdef), advance
///   600/1000 × 12 = 7.2 px.</item>
/// </list></para></summary>
public sealed class InlineLayouterCycle3cTests
{
    private const string LatnScript = "Latn";
    private const string EnLang = "en";

    // Per-glyph advances under SyntheticFont (fontSizePx=12, UPEM=1000).
    private const double LetterAdvance = 6.0;   // 'A' / 'B' → glyph 1/2 advance 500/1000*12
    private const double SpaceAdvance = 7.2;    // SP → .notdef glyph 0 advance 600/1000*12

    [Fact]
    public void LayoutPerRun_NoWrap_span_inside_Normal_wraps_at_outer_boundary()
    {
        // Two runs: outer Normal "AAA " + inner NoWrap "BBB BBB".
        // Concat = "AAA BBB BBB" (11 chars / 11 glyphs after shaping).
        // Available = 25 px → fits "AAA" only (3 letters = 18 px;
        // adding the SP makes 25.2 > 25, snapping wrap to lastAllowed
        // at the Allowed opportunity AFTER the SP). Line 1 holds
        // "BBB BBB" as one unbreakable run (NoWrap suppresses the
        // inner SP's Allowed opportunity per cycle 3c per-glyph
        // honoring) — overflowing the budget but staying together.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AAA ", MakeStyle()),
            new("BBB BBB", MakeStyleWithNoWrap()),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 25, resolver, LatnScript, EnLang);

        // Exactly 2 lines.
        Assert.Equal(2, result.Length);

        // --- Line 0: "AAA" (3 letters from source run 0, trailing
        // SP trimmed at the wrap boundary per CSS Text L3 §4.1.2).
        var line0 = result[0];
        Assert.False(line0.EndsWithMandatoryBreak);
        Assert.Single(line0.Slices);
        Assert.Equal(0, line0.Slices[0].ShapedRunIndex);
        Assert.Equal(0, line0.Slices[0].GlyphStart);
        Assert.Equal(3, line0.Slices[0].GlyphLength);
        Assert.Equal(3 * LetterAdvance, line0.Slices[0].SliceAdvance, precision: 4);
        Assert.Equal(3 * LetterAdvance, line0.TotalAdvance, precision: 4);

        // --- Line 1: full "BBB BBB" from source run 1 — NoWrap span
        // stays together, the inner SP's Allowed opp was downgraded.
        var line1 = result[1];
        Assert.Single(line1.Slices);
        Assert.Equal(1, line1.Slices[0].ShapedRunIndex);
        Assert.Equal(0, line1.Slices[0].GlyphStart);
        // 7 glyphs = "BBB BBB". No internal split possible — proves
        // the per-glyph honoring suppressed the inner SP's Allowed.
        Assert.Equal(7, line1.Slices[0].GlyphLength);
        Assert.Equal(6 * LetterAdvance + SpaceAdvance,
            line1.Slices[0].SliceAdvance, precision: 4);
        Assert.Equal(6 * LetterAdvance + SpaceAdvance,
            line1.TotalAdvance, precision: 4);
    }

    [Fact]
    public void LayoutPerRun_Normal_run_after_NoWrap_still_wraps_at_its_own_boundary()
    {
        // Order matters: cycle 3b/3c initial impl took the FIRST
        // non-empty source run's WhiteSpace as the "effective"
        // policy, so a leading NoWrap source run made the WHOLE
        // wrap pass non-wrapping — including a trailing Normal
        // source run that SHOULD wrap. Cycle 3c review Rec #2 fixed
        // this by making the global wrapsAtAllowed gate true
        // whenever whiteSpacePerRun is supplied, deferring per-glyph
        // suppression to the downgrade pass.
        //
        // Source: "BBB " (NoWrap) + "AAA AAA" (Normal). Available =
        // 25 → must wrap at the SP inside the Normal run.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("BBB ", MakeStyleWithNoWrap()),
            new("AAA AAA", MakeStyle()),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 25, resolver, LatnScript, EnLang);

        // 2 lines proves the Normal run's Allowed opp was honored
        // (would be a single overflowing line if Rec #2 wasn't fixed).
        Assert.Equal(2, result.Length);

        // --- Line 0 spans BOTH shaped runs: SR0 [0..4) (all of "BBB ")
        // + SR1 [0..3) ("AAA"). SP at end of SR0 (concat pos 3) is
        // NOT at a wrap point (its Allowed was downgraded under
        // NoWrap), so it's kept in the line. Wrap snaps at the SP
        // INSIDE SR1 (concat pos 7), with that SP trimmed.
        var line0 = result[0];
        Assert.False(line0.EndsWithMandatoryBreak);
        Assert.Equal(2, line0.Slices.Length);
        Assert.Equal(0, line0.Slices[0].ShapedRunIndex);
        Assert.Equal(0, line0.Slices[0].GlyphStart);
        Assert.Equal(4, line0.Slices[0].GlyphLength); // "BBB " incl trailing SP
        Assert.Equal(1, line0.Slices[1].ShapedRunIndex);
        Assert.Equal(0, line0.Slices[1].GlyphStart);
        Assert.Equal(3, line0.Slices[1].GlyphLength); // "AAA" only
        // TotalAdvance = "BBB " (18 + 7.2 = 25.2) + "AAA" (18) = 43.2.
        Assert.Equal(3 * LetterAdvance + SpaceAdvance + 3 * LetterAdvance,
            line0.TotalAdvance, precision: 4);

        // --- Line 1: SR1 [4..7) ("AAA").
        var line1 = result[1];
        Assert.Single(line1.Slices);
        Assert.Equal(1, line1.Slices[0].ShapedRunIndex);
        Assert.Equal(4, line1.Slices[0].GlyphStart);
        Assert.Equal(3, line1.Slices[0].GlyphLength);
        Assert.Equal(3 * LetterAdvance, line1.TotalAdvance, precision: 4);
    }

    [Fact]
    public void LayoutPerRun_uniform_NoWrap_emits_single_overflowing_line()
    {
        // All runs share NoWrap → no per-run array built; uniform
        // path delegates correctly with whiteSpace=NoWrap.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AAA AAA", MakeStyleWithNoWrap()),
            new(" BBB", MakeStyleWithNoWrap()),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 25, resolver, LatnScript, EnLang);

        // NoWrap → single overflowing line.
        Assert.Single(result);
        Assert.True(result[0].EndsWithMandatoryBreak);
        Assert.Equal(2, result[0].Slices.Length);
        Assert.Equal(0, result[0].Slices[0].ShapedRunIndex);
        Assert.Equal(0, result[0].Slices[0].GlyphStart);
        Assert.Equal(7, result[0].Slices[0].GlyphLength); // "AAA AAA"
        Assert.Equal(1, result[0].Slices[1].ShapedRunIndex);
        Assert.Equal(0, result[0].Slices[1].GlyphStart);
        Assert.Equal(4, result[0].Slices[1].GlyphLength); // " BBB"
    }

    [Fact]
    public void LayoutPerRun_mixed_overflow_wrap_now_handled_per_glyph()
    {
        // Cycle 3c only handled WhiteSpace mismatch; cycle 3d
        // sub-cycle 2 broadens to overflow-wrap mismatch via
        // per-source-run plumbing through LineBuilder.Wrap's
        // `overflowWrapPerRun` parameter. The anywhere fallback
        // gates per-glyph by source-run-index.
        using var resolver = new TestShaperResolver();
        var sNormal = MakeStyle();
        var sAnywhere = ComputedStyle.RentForExclusiveTesting();
        sAnywhere.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere
        var sourceRuns = new List<TextRun>
        {
            new("AAA", sNormal),
            new("BBB", sAnywhere),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns, 15, resolver,
            LatnScript, EnLang);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void LayoutPerRun_mixed_hyphens_now_handled_per_run()
    {
        // Cycle 3d sub-cycle 4 — Hyphens mismatch is now handled.
        using var resolver = new TestShaperResolver();
        var sManual = MakeStyle();
        var sAuto = ComputedStyle.RentForExclusiveTesting();
        sAuto.Set(PropertyId.Hyphens, ComputedSlot.FromKeyword(2)); // auto
        var sourceRuns = new List<TextRun>
        {
            new("foo", sManual),
            new("bar", sAuto),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns, 100, resolver,
            LatnScript, EnLang);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void Wrap_with_whiteSpacePerRun_length_mismatch_throws()
    {
        // Direct LineBuilder.Wrap test: per-run array length must
        // match sourceTextRuns count.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AAA", MakeStyle()),
            new("BBB", MakeStyle()),
        };
        var itemized = LineBuilder.Itemize(sourceRuns,
            NetPdf.Text.Bidi.ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver,
            LatnScript, EnLang);

        // Per cycle 3d sub-cycle 2 Rec #4 refactor — single per-run
        // InlineTextPolicy[] array replaces the cycle 3c
        // whiteSpacePerRun parallel array; the length-validation
        // moved to the new parameter.
        var bogusPerRun = new[]
        {
            InlineTextPolicy.Default,
        }; // length 1 != 2

        Assert.Throws<ArgumentException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, 100,
                inlineTextPolicyPerRun: bogusPerRun));
    }

    // --- Cycle 3c review hardening (Recs #1+#3+#6 + Copilot) ------

    // The 4 matrix-narrowing throw tests that used to live here
    // (Normal+Pre, NoWrap+PreWrap, Normal+PreLine, Normal+BreakSpaces)
    // were removed when Phase 3 Task 10 cycle 3d sub-cycle 1 broadened
    // the LayoutPerRun WhiteSpace matrix to the full six-value set
    // via the new per-source-run preprocessor. See
    // [`InlineLayouterCycle3dTests`](InlineLayouterCycle3dTests.cs)
    // for the now-handled behavior with exact assertions.

    [Fact]
    public void Wrap_with_whiteSpacePerRun_invalid_enum_value_throws()
    {
        // Per cycle 3c review Rec #6 — undefined enum values in the
        // whiteSpacePerRun array must throw at entry instead of
        // silently producing indeterminate wrap behavior (an
        // undefined value is "not Pre or NoWrap" so the per-glyph
        // downgrade switch falls through, silently leaving Allowed
        // opps in place).
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AAA", MakeStyle()),
            new("BBB", MakeStyle()),
        };
        var itemized = LineBuilder.Itemize(sourceRuns,
            NetPdf.Text.Bidi.ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver,
            LatnScript, EnLang);

        // Per cycle 3d sub-cycle 2 Rec #4 refactor — invalid enum
        // validation moved to the inlineTextPolicyPerRun parameter.
        var perRun = new[]
        {
            InlineTextPolicy.Default,
            new InlineTextPolicy((WhiteSpace)99,
                OverflowWrap.Normal, WordBreak.Normal, Hyphens.Manual),
        };

        var ex = Assert.Throws<ArgumentException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, 100,
                inlineTextPolicyPerRun: perRun));
        Assert.Contains("inlineTextPolicyPerRun[1].WhiteSpace", ex.Message);
    }

    [Fact]
    public void LayoutPerRun_NoWrap_first_then_Normal_collapses_outer_double_space()
    {
        // Per cycle 3c review Rec #1 — when LayoutPerRun preprocesses
        // a Normal+NoWrap mix it uses WhiteSpace.Normal globally
        // (both members of the matrix share collapse semantics per
        // CSS Text L3 §4.1). This test pins that intra-run double-
        // whitespace gets collapsed to one SP in both Normal AND
        // NoWrap runs (proving the global Normal preprocess applies
        // uniformly + doesn't preserve NoWrap's whitespace as the
        // earlier PreWrap-everywhere bug would have).
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AAA  ", MakeStyleWithNoWrap()), // 2 trailing SPs
            new("BBB", MakeStyle()),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 1000, resolver, LatnScript, EnLang);

        Assert.Single(result);
        // After Normal collapse: "AAA " (4 glyphs) + "BBB" (3 glyphs)
        // = 7 glyphs total. If the buggy PreWrap-everywhere preprocess
        // had run, we'd see "AAA  " (5 glyphs) + "BBB" = 8.
        Assert.Equal(2, result[0].Slices.Length);
        Assert.Equal(0, result[0].Slices[0].ShapedRunIndex);
        Assert.Equal(4, result[0].Slices[0].GlyphLength);
        Assert.Equal(1, result[0].Slices[1].ShapedRunIndex);
        Assert.Equal(3, result[0].Slices[1].GlyphLength);
        // Total advance: "AAA " = 3×6 + 7.2 = 25.2, "BBB" = 18, sum 43.2.
        Assert.Equal(3 * LetterAdvance + SpaceAdvance + 3 * LetterAdvance,
            result[0].TotalAdvance, precision: 4);
    }

    [Fact]
    public void LayoutPerRun_uniform_Normal_path_unaffected_by_review_changes()
    {
        // Regression guard: cycle 3c review changes (Recs #1+#3+#4)
        // touched the LayoutPerRun delegation path. Pin that the
        // uniform-Normal case (whiteSpacePerRun is null, policy.
        // WhiteSpace = Normal, single preprocess pass) still produces
        // the same per-line output cycle 3a/3b did.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AAA ", MakeStyle()),
            new("AAA", MakeStyle()),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 1000, resolver, LatnScript, EnLang);

        Assert.Single(result);
        Assert.Equal(2, result[0].Slices.Length);
        Assert.Equal(0, result[0].Slices[0].ShapedRunIndex);
        Assert.Equal(4, result[0].Slices[0].GlyphLength);
        Assert.Equal(1, result[0].Slices[1].ShapedRunIndex);
        Assert.Equal(3, result[0].Slices[1].GlyphLength);
    }

    // --- Helpers --------------------------------------------------

    private static ComputedStyle MakeStyle() =>
        ComputedStyle.RentForExclusiveTesting();

    private static ComputedStyle MakeStyleWithNoWrap()
    {
        var s = ComputedStyle.RentForExclusiveTesting();
        s.Set(PropertyId.WhiteSpace, ComputedSlot.FromKeyword(2)); // nowrap
        return s;
    }

    private sealed class TestShaperResolver : IShaperResolver
    {
        private readonly HbShaper _shaper = new(SyntheticFont.Build(), fontSizePx: 12);
        public HbShaper Resolve(ComputedStyle style) => _shaper;
        public void Dispose() => _shaper.Dispose();
    }
}
