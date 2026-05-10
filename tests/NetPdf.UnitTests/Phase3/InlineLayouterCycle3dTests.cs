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

/// <summary>Per Phase 3 Task 10 cycle 3d sub-cycle 1 — tests for the
/// per-source-run WhiteSpace preprocessor + broadened
/// <see cref="InlineLayouter.LayoutPerRun"/> matrix.
///
/// <para>Cycle 3c narrowed the accepted mixed-WhiteSpace matrix to
/// {Normal, NoWrap}-only because the preprocessor was uniform-mode
/// (collapse-vs-preserve cannot be reconciled post-shape with a
/// single mode). Cycle 3d sub-cycle 1 ships
/// <see cref="LineBuilder.PreprocessTextRunsPerRun"/>: each source
/// run is preprocessed with its OWN <see cref="WhiteSpace"/> mode,
/// with cross-run state managed per the "preserve runs are atomic;
/// collapse runs chain via inWs" rule. <c>LayoutPerRun</c>
/// now accepts the FULL six-value WhiteSpace mismatch matrix.</para>
///
/// <para><b>Synthetic font advance reference.</b> The
/// <see cref="SyntheticFont"/> at fontSizePx=12 + UPEM=1000 yields:
/// <list type="bullet">
///   <item>'A' / 'B' → glyph 1 / 2, advance 500/1000 × 12 = 6.0 px.</item>
///   <item>Anything else (incl. ' ') → glyph 0 (.notdef), advance
///   600/1000 × 12 = 7.2 px.</item>
/// </list></para></summary>
public sealed class InlineLayouterCycle3dTests
{
    private const string LatnScript = "Latn";
    private const string EnLang = "en";

    private const double LetterAdvance = 6.0;
    private const double SpaceAdvance = 7.2;

    // --- Direct PreprocessTextRunsPerRun tests --------------------

    [Fact]
    public void PreprocessTextRunsPerRun_length_mismatch_throws()
    {
        var runs = new List<TextRun>
        {
            new("AAA", MakeStyle()),
            new("BBB", MakeStyle()),
        };
        var modes = new[] { WhiteSpace.Normal }; // length 1 != 2

        var ex = Assert.Throws<ArgumentException>(() =>
            LineBuilder.PreprocessTextRunsPerRun(runs, modes));
        Assert.Contains("modes length", ex.Message);
    }

    [Fact]
    public void PreprocessTextRunsPerRun_invalid_enum_value_throws()
    {
        var runs = new List<TextRun>
        {
            new("AAA", MakeStyle()),
            new("BBB", MakeStyle()),
        };
        var modes = new[] { WhiteSpace.Normal, (WhiteSpace)99 };

        var ex = Assert.Throws<ArgumentException>(() =>
            LineBuilder.PreprocessTextRunsPerRun(runs, modes));
        Assert.Contains("modes[1]", ex.Message);
    }

    [Fact]
    public void PreprocessTextRunsPerRun_null_runs_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LineBuilder.PreprocessTextRunsPerRun(
                null!, new[] { WhiteSpace.Normal }));
    }

    [Fact]
    public void PreprocessTextRunsPerRun_null_modes_throws()
    {
        var runs = new List<TextRun> { new("AAA", MakeStyle()) };
        Assert.Throws<ArgumentNullException>(() =>
            LineBuilder.PreprocessTextRunsPerRun(runs, null!));
    }

    [Fact]
    public void PreprocessTextRunsPerRun_uniform_Normal_matches_PreprocessTextRuns()
    {
        // Regression — uniform-Normal via the per-run API must produce
        // byte-identical output as the existing PreprocessTextRuns.
        var runs = new List<TextRun>
        {
            new("  AAA  ", MakeStyle()),
            new("  BBB  ", MakeStyle()),
        };
        var perRun = LineBuilder.PreprocessTextRunsPerRun(runs,
            new[] { WhiteSpace.Normal, WhiteSpace.Normal });
        var uniform = LineBuilder.PreprocessTextRuns(runs,
            WhiteSpace.Normal);

        Assert.Equal(uniform.Count, perRun.Count);
        for (var i = 0; i < uniform.Count; i++)
        {
            Assert.Equal(uniform[i].Text, perRun[i].Text);
        }
    }

    [Fact]
    public void PreprocessTextRunsPerRun_Pre_then_Normal_preserves_Pre_collapses_Normal()
    {
        // Pre run "AA  " preserves its 2 trailing SPs. Normal run
        // "  BB" with inWs=false (reset after Pre) collapses 2 leading
        // SPs to 1. Result: "AA  " + " BB" = "AA   BB" (3 SPs at
        // boundary).
        var runs = new List<TextRun>
        {
            new("AA  ", MakeStyle()),
            new("  BB", MakeStyle()),
        };
        var output = LineBuilder.PreprocessTextRunsPerRun(runs,
            new[] { WhiteSpace.Pre, WhiteSpace.Normal });

        Assert.Equal(2, output.Count);
        Assert.Equal("AA  ", output[0].Text); // Pre preserved as-is
        Assert.Equal(" BB", output[1].Text);  // Normal collapsed 2→1
    }

    [Fact]
    public void PreprocessTextRunsPerRun_Normal_then_Pre_preserves_both()
    {
        // Normal run "AA  " collapses 2 trailing SPs to 1 (and would
        // strip if last run, but it isn't). Pre run "  BB" preserves
        // its 2 leading SPs. Result: "AA " + "  BB" = "AA   BB".
        var runs = new List<TextRun>
        {
            new("AA  ", MakeStyle()),
            new("  BB", MakeStyle()),
        };
        var output = LineBuilder.PreprocessTextRunsPerRun(runs,
            new[] { WhiteSpace.Normal, WhiteSpace.Pre });

        Assert.Equal(2, output.Count);
        Assert.Equal("AA ", output[0].Text); // Normal collapsed 2→1
        Assert.Equal("  BB", output[1].Text); // Pre preserved
    }

    [Fact]
    public void PreprocessTextRunsPerRun_Pre_only_does_NOT_strip_document_trailing_SP()
    {
        // Single Pre run with trailing SP — document-trailing strip
        // only fires for collapse modes per the per-run preprocessor.
        var runs = new List<TextRun>
        {
            new("AAA ", MakeStyle()),
        };
        var output = LineBuilder.PreprocessTextRunsPerRun(runs,
            new[] { WhiteSpace.Pre });

        Assert.Single(output);
        Assert.Equal("AAA ", output[0].Text); // SP preserved
    }

    [Fact]
    public void PreprocessTextRunsPerRun_Normal_only_strips_document_trailing_SP()
    {
        // Single Normal run with trailing SP — document-trailing strip
        // fires (the standard collapse-mode CSS rule).
        var runs = new List<TextRun>
        {
            new("AAA ", MakeStyle()),
        };
        var output = LineBuilder.PreprocessTextRunsPerRun(runs,
            new[] { WhiteSpace.Normal });

        Assert.Single(output);
        Assert.Equal("AAA", output[0].Text); // trailing SP stripped
    }

    // --- LayoutPerRun broadened-matrix tests ---------------------

    [Fact]
    public void LayoutPerRun_Normal_mixed_with_Pre_now_handled()
    {
        // Normal "AAA " (4 chars) + Pre " BBB" (4 chars, leading SP
        // preserved). Cycle 3c threw NotSupportedException; cycle 3d
        // handles it: per-run preprocessor preserves Pre's leading SP
        // + collapses Normal's whitespace. Per-glyph downgrade
        // suppresses wraps inside the Pre run.
        using var resolver = new TestShaperResolver();
        var sNormal = MakeStyle();
        var sPre = ComputedStyle.RentForExclusiveTesting();
        sPre.Set(PropertyId.WhiteSpace, ComputedSlot.FromKeyword(1)); // pre
        var sourceRuns = new List<TextRun>
        {
            new("AAA ", sNormal),
            new(" BBB", sPre),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 1000, resolver, LatnScript, EnLang);

        // Available 1000 → single line.
        Assert.Single(result);
        Assert.Equal(2, result[0].Slices.Length);
        // Slice 0: SR0 [0..4) — Normal "AAA " (4 glyphs, trailing SP
        // not stripped because not last run).
        Assert.Equal(0, result[0].Slices[0].ShapedRunIndex);
        Assert.Equal(0, result[0].Slices[0].GlyphStart);
        Assert.Equal(4, result[0].Slices[0].GlyphLength);
        // Slice 1: SR1 [0..4) — Pre " BBB" (4 glyphs, leading SP
        // preserved).
        Assert.Equal(1, result[0].Slices[1].ShapedRunIndex);
        Assert.Equal(0, result[0].Slices[1].GlyphStart);
        Assert.Equal(4, result[0].Slices[1].GlyphLength);
        // Total advance: "AAA " = 3×6 + 7.2 = 25.2; " BBB" = 7.2 + 18 = 25.2.
        Assert.Equal(2 * (3 * LetterAdvance + SpaceAdvance),
            result[0].TotalAdvance, precision: 4);
    }

    [Fact]
    public void LayoutPerRun_NoWrap_mixed_with_PreWrap_now_handled()
    {
        // NoWrap "AAA" + PreWrap " BBB". PreWrap preserves whitespace
        // BUT wraps at Allowed (Pre wraps differently). The per-run
        // preprocessor preserves PreWrap content; the per-glyph
        // downgrade keeps the PreWrap run's Allowed opps (NoWrap
        // would suppress them).
        using var resolver = new TestShaperResolver();
        var sNoWrap = MakeStyleWithNoWrap();
        var sPreWrap = ComputedStyle.RentForExclusiveTesting();
        sPreWrap.Set(PropertyId.WhiteSpace, ComputedSlot.FromKeyword(3)); // pre-wrap
        var sourceRuns = new List<TextRun>
        {
            new("AAA", sNoWrap),
            new(" BBB", sPreWrap),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 1000, resolver, LatnScript, EnLang);

        Assert.Single(result);
        Assert.Equal(2, result[0].Slices.Length);
        Assert.Equal(0, result[0].Slices[0].ShapedRunIndex);
        Assert.Equal(3, result[0].Slices[0].GlyphLength); // "AAA"
        Assert.Equal(1, result[0].Slices[1].ShapedRunIndex);
        Assert.Equal(4, result[0].Slices[1].GlyphLength); // " BBB"
    }

    [Fact]
    public void LayoutPerRun_Normal_mixed_with_PreLine_now_handled()
    {
        // Normal "AAA " + PreLine "BBB " (collapses SP/TAB but
        // preserves LF). PreLine has no LF in this test → behaves
        // like Normal for whitespace. Both are collapse modes →
        // inWs chains across the boundary.
        using var resolver = new TestShaperResolver();
        var sNormal = MakeStyle();
        var sPreLine = ComputedStyle.RentForExclusiveTesting();
        sPreLine.Set(PropertyId.WhiteSpace, ComputedSlot.FromKeyword(4)); // pre-line
        var sourceRuns = new List<TextRun>
        {
            new("AAA ", sNormal),
            new("BBB", sPreLine),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 1000, resolver, LatnScript, EnLang);

        Assert.Single(result);
        Assert.Equal(2, result[0].Slices.Length);
        Assert.Equal(4, result[0].Slices[0].GlyphLength); // "AAA "
        Assert.Equal(3, result[0].Slices[1].GlyphLength); // "BBB"
    }

    [Fact]
    public void LayoutPerRun_Normal_mixed_with_BreakSpaces_now_handled()
    {
        // BreakSpaces is a preserve mode (folds to PreWrap-like
        // behavior in cycle 3 — see WhiteSpace.cs). The per-run
        // preprocessor treats it as preserve.
        using var resolver = new TestShaperResolver();
        var sNormal = MakeStyle();
        var sBreakSpaces = ComputedStyle.RentForExclusiveTesting();
        sBreakSpaces.Set(PropertyId.WhiteSpace, ComputedSlot.FromKeyword(5)); // break-spaces
        var sourceRuns = new List<TextRun>
        {
            new("AAA", sNormal),
            new(" BBB", sBreakSpaces),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 1000, resolver, LatnScript, EnLang);

        Assert.Single(result);
        Assert.Equal(2, result[0].Slices.Length);
        Assert.Equal(3, result[0].Slices[0].GlyphLength); // "AAA"
        Assert.Equal(4, result[0].Slices[1].GlyphLength); // " BBB" preserved
    }

    [Fact]
    public void LayoutPerRun_Pre_first_then_Normal_handled()
    {
        // Pre "AA  " (preserves trailing SPs) + Normal "  BB" (with
        // inWs=false after Pre, collapses 2 leading SPs to 1). Both
        // runs' content preserved differently. Concat: "AA   BB".
        using var resolver = new TestShaperResolver();
        var sPre = ComputedStyle.RentForExclusiveTesting();
        sPre.Set(PropertyId.WhiteSpace, ComputedSlot.FromKeyword(1)); // pre
        var sNormal = MakeStyle();
        var sourceRuns = new List<TextRun>
        {
            new("AA  ", sPre),
            new("  BB", sNormal),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 1000, resolver, LatnScript, EnLang);

        Assert.Single(result);
        Assert.Equal(2, result[0].Slices.Length);
        // Slice 0: Pre "AA  " → 4 glyphs (preserved).
        Assert.Equal(0, result[0].Slices[0].ShapedRunIndex);
        Assert.Equal(4, result[0].Slices[0].GlyphLength);
        // Slice 1: Normal "  BB" → collapsed to " BB" → 3 glyphs.
        Assert.Equal(1, result[0].Slices[1].ShapedRunIndex);
        Assert.Equal(3, result[0].Slices[1].GlyphLength);
    }

    [Fact]
    public void LayoutPerRun_mixed_word_break_still_throws()
    {
        // Cycle 3d sub-cycle 2 broadens the matrix to overflow-wrap
        // mismatches too. The remaining "still throws" cases are
        // word-break + hyphens mismatches (per-glyph metadata for
        // those deferred to sub-cycle 3+).
        using var resolver = new TestShaperResolver();
        var sNormal = MakeStyle();
        var sBreakAll = ComputedStyle.RentForExclusiveTesting();
        sBreakAll.Set(PropertyId.WordBreak, ComputedSlot.FromKeyword(1)); // break-all
        var sourceRuns = new List<TextRun>
        {
            new("AAA", sNormal),
            new("BBB", sBreakAll),
        };
        var ex = Assert.Throws<NotSupportedException>(() =>
            InlineLayouter.LayoutPerRun(sourceRuns, 100, resolver,
                LatnScript, EnLang));
        Assert.Contains("word-break or hyphens", ex.Message);
    }

    [Fact]
    public void LayoutPerRun_mixed_hyphens_still_throws()
    {
        // Cycle 3d sub-cycle 2 broadens to overflow-wrap mismatches;
        // hyphens mismatch still throws (sub-cycle 3+ scope).
        using var resolver = new TestShaperResolver();
        var sManual = MakeStyle();
        var sAuto = ComputedStyle.RentForExclusiveTesting();
        sAuto.Set(PropertyId.Hyphens, ComputedSlot.FromKeyword(2)); // auto
        var sourceRuns = new List<TextRun>
        {
            new("foo", sManual),
            new("bar", sAuto),
        };
        var ex = Assert.Throws<NotSupportedException>(() =>
            InlineLayouter.LayoutPerRun(sourceRuns, 100, resolver,
                LatnScript, EnLang));
        Assert.Contains("word-break or hyphens", ex.Message);
    }

    // --- Cycle 3d sub-cycle 2: per-glyph overflow-wrap tests -----

    [Fact]
    public void LayoutPerRun_anywhere_in_one_run_does_NOT_break_inside_normal_overflow_wrap_run()
    {
        // Source: Normal(overflow-wrap:normal) "AAAA" (4 glyphs) +
        // Normal(overflow-wrap:anywhere) "BBBBB" (5 glyphs).
        // Budget = 10 (very small).
        //
        // overflowWrapPerRun = [Normal, Anywhere].
        //
        // - cursor 0-3 (Run 0, normal overflow-wrap):
        //   afterAdvance grows past 10. No Allowed candidate
        //   (Normal letters → Prohibited). Anywhere check at each
        //   cursor: cursor's source-run is Run 0 = Normal overflow-
        //   wrap → anywhereAllowedHere=false → don't fire.
        // - cursor 4 (Run 1, Anywhere): cursor=4, lineStart=0,
        //   cursor-1=3 (Run 0, Normal) → different src runs →
        //   anywhereAllowedHere = prev.Normal || cursor.Anywhere
        //   = true. Fire. Emit [0..3] (4 glyphs "AAAA").
        //   lineStart=4.
        // - cursor 4 (Run 1): cum 6.
        // - cursor 5 (Run 1): cum 12 > 10. Same-run anywhere check:
        //   cursor's run is Anywhere → fire. Emit [4..4]. lineStart=5.
        // - ... each subsequent B emits alone.
        using var resolver = new TestShaperResolver();
        var sNormal = MakeStyle();
        var sAnywhere = ComputedStyle.RentForExclusiveTesting();
        sAnywhere.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere
        var sourceRuns = new List<TextRun>
        {
            new("AAAA", sNormal),
            new("BBBBB", sAnywhere),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 10, resolver, LatnScript, EnLang);

        // Line 0 = the entire "AAAA" run (4 glyphs in SR 0) —
        // anywhere did NOT split inside the normal-overflow-wrap run.
        Assert.True(result.Length >= 1);
        Assert.Single(result[0].Slices);
        Assert.Equal(0, result[0].Slices[0].ShapedRunIndex);
        Assert.Equal(0, result[0].Slices[0].GlyphStart);
        Assert.Equal(4, result[0].Slices[0].GlyphLength);
        // The B run got split per anywhere (multiple lines after line 0).
        Assert.True(result.Length >= 2);
    }

    [Fact]
    public void LayoutPerRun_anywhere_in_one_run_DOES_break_inside_anywhere_run()
    {
        // Inverse of the test above — when the anywhere run is the
        // one being walked, the forced break DOES fire inside it.
        // Source: Normal(anywhere) "AAAAA" (5 glyphs) +
        // Normal(normal overflow-wrap) "BBBB" (4 glyphs). Budget = 10.
        //
        // overflowWrapPerRun = [Anywhere, Normal].
        // - cursor 0-1: cum 6, 12 > 10. cursor=1, prev=0, both in
        //   Run 0 = Anywhere → fire. Emit [0..0] ("A"). lineStart=1.
        // - cursor 1: cum 6. cursor 2: cum 12 > 10. Fire. lineStart=2.
        // - ... Run 0 splits into 5 single-glyph lines.
        // - At Run 1 (BBBB normal): no further forced splits inside
        //   (would overflow as one line).
        using var resolver = new TestShaperResolver();
        var sAnywhere = ComputedStyle.RentForExclusiveTesting();
        sAnywhere.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere
        var sNormal = MakeStyle();
        var sourceRuns = new List<TextRun>
        {
            new("AAAAA", sAnywhere),
            new("BBBB", sNormal),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 10, resolver, LatnScript, EnLang);

        // The Anywhere run got split (≥ 2 lines). Pre-fix
        // (sub-cycle 1 threw on overflow-wrap mismatch) we wouldn't
        // even reach this code path.
        Assert.True(result.Length >= 2,
            $"Anywhere run should split into multiple lines; got {result.Length}.");
    }

    [Fact]
    public void Wrap_overflowWrapPerRun_length_mismatch_throws()
    {
        // Validate the new overflowWrapPerRun parameter rejects
        // length mismatches.
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

        var bogusPerRun = new[] { OverflowWrap.Normal }; // length 1 != 2

        Assert.Throws<ArgumentException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, 100,
                overflowWrapPerRun: bogusPerRun));
    }

    [Fact]
    public void Wrap_overflowWrapPerRun_invalid_enum_value_throws()
    {
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

        var perRun = new[] { OverflowWrap.Normal, (OverflowWrap)99 };

        var ex = Assert.Throws<ArgumentException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, 100,
                overflowWrapPerRun: perRun));
        Assert.Contains("overflowWrapPerRun[1]", ex.Message);
    }

    // --- Cycle 3d sub-cycle 1 review hardening tests --------------

    [Fact]
    public void LayoutPerRun_PreWrap_trailing_SP_preserved_at_soft_wrap_boundary()
    {
        // Per cycle 3d sub-cycle 1 review Rec #1 — preserved spaces
        // (Pre/PreWrap/BreakSpaces) must NOT be trimmed at soft-wrap
        // boundaries (they hang per CSS Text L3 §6.4 / preserve per
        // §4.1.2). This test pins the regression: PreWrap "AAA "
        // followed by Normal " BBB" with a budget that wraps right
        // after the PreWrap trailing SP (the snap-back lastAllowed
        // candidate). Pre-fix the SP would be tagged IsBreakSpace
        // (because wrapWhiteSpace=Normal globally → collapsesSpaces
        // applied to ALL glyphs) and trimmed off line 0; post-fix
        // the per-glyph IsBreakSpace correctly identifies the SP as
        // belonging to a preserve-mode run, so it stays in the
        // drawable slice.
        //
        // Layout for budget 30:
        //   PreWrap "AAA " = 4 glyphs (3*'A'=18 + SP=7.2 = 25.2px).
        //   Normal " BBB"  = 4 glyphs (the leading SP not collapsed
        //                     by Normal because inWs=false after Pre
        //                     → SP output, then BBB).
        //   At cursor=3 (PreWrap SP): cum 25.2 < 30, opp=Allowed
        //                             (PreWrap = wrap-friendly). Record.
        //   At cursor=4 (Normal SP):  cum 32.4 > 30. Snap to lastAllowed=3.
        //                             drawableEnd=3.
        //   Trim IsBreakSpace: glyph 3 has IsBreakSpace=false (PreWrap)
        //                      → NO trim. drawableEnd stays 3.
        //   Emit 0..3 → 4 glyphs "AAA " (SP PRESERVED).
        using var resolver = new TestShaperResolver();
        var sPreWrap = ComputedStyle.RentForExclusiveTesting();
        sPreWrap.Set(PropertyId.WhiteSpace, ComputedSlot.FromKeyword(3)); // pre-wrap
        var sNormal = MakeStyle();
        var sourceRuns = new List<TextRun>
        {
            new("AAA ", sPreWrap),
            new(" BBB", sNormal),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 30, resolver, LatnScript, EnLang);

        Assert.Equal(2, result.Length);
        // --- Line 0: "AAA " (4 glyphs, trailing SP preserved per
        // PreWrap semantics — Rec #1 fix).
        Assert.False(result[0].EndsWithMandatoryBreak);
        Assert.Single(result[0].Slices);
        Assert.Equal(0, result[0].Slices[0].ShapedRunIndex);
        Assert.Equal(0, result[0].Slices[0].GlyphStart);
        Assert.Equal(4, result[0].Slices[0].GlyphLength);
        // Total advance INCLUDES the preserved trailing SP.
        Assert.Equal(3 * LetterAdvance + SpaceAdvance,
            result[0].TotalAdvance, precision: 4);
    }

    [Fact]
    public void LayoutPerRun_anywhere_does_NOT_split_inside_NoWrap_run()
    {
        // Per cycle 3d sub-cycle 1 review Rec #2 — when all runs
        // share overflow-wrap:anywhere AND a NoWrap run exists, the
        // anywhere fallback must NOT force a break inside the NoWrap
        // span. Per CSS Text L3 §5.1, overflow-wrap "only has an
        // effect when white-space allows wrapping".
        //
        // Source: NoWrap+anywhere "AAAAA" (5 glyphs, all
        // Allowed→Prohibited downgraded) + Normal+anywhere "B"
        // (1 glyph). Available = 10 (very small).
        //   - cursors 0..4: cum grows past 10. lastAllowed=-1 (NoWrap
        //     downgrade kills candidates). Anywhere check: prev/cursor
        //     in same NoWrap run → gate fails → no fire.
        //   - cursor 5 ('B', Normal run): different src run from
        //     cursor 4 → gate passes → anywhere fires at run boundary.
        //     Emit [0..4] (NoWrap stays intact). lineStart=5.
        //   - cursor 5: emit [5..5] alone (single overflowing 'B').
        using var resolver = new TestShaperResolver();
        var sNoWrapAnywhere = ComputedStyle.RentForExclusiveTesting();
        sNoWrapAnywhere.Set(PropertyId.WhiteSpace, ComputedSlot.FromKeyword(2)); // nowrap
        sNoWrapAnywhere.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere
        var sNormalAnywhere = ComputedStyle.RentForExclusiveTesting();
        sNormalAnywhere.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere
        var sourceRuns = new List<TextRun>
        {
            new("AAAAA", sNoWrapAnywhere),
            new("B", sNormalAnywhere),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 10, resolver, LatnScript, EnLang);

        // Line 0: NoWrap stays whole (5 glyphs). Pre-fix anywhere
        // would have split inside it.
        Assert.True(result.Length >= 1);
        Assert.Single(result[0].Slices);
        Assert.Equal(0, result[0].Slices[0].ShapedRunIndex);
        Assert.Equal(0, result[0].Slices[0].GlyphStart);
        Assert.Equal(5, result[0].Slices[0].GlyphLength);
    }

    [Fact]
    public void LayoutPerRun_anywhere_does_NOT_split_inside_Pre_run()
    {
        // Per cycle 3d sub-cycle 1 review Rec #2 — same gating for
        // Pre runs.
        using var resolver = new TestShaperResolver();
        var sPreAnywhere = ComputedStyle.RentForExclusiveTesting();
        sPreAnywhere.Set(PropertyId.WhiteSpace, ComputedSlot.FromKeyword(1)); // pre
        sPreAnywhere.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere
        var sNormalAnywhere = ComputedStyle.RentForExclusiveTesting();
        sNormalAnywhere.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere
        var sourceRuns = new List<TextRun>
        {
            new("AAAAA", sPreAnywhere),
            new("B", sNormalAnywhere),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 10, resolver, LatnScript, EnLang);

        Assert.True(result.Length >= 1);
        // First slice is the entire 5-glyph Pre run, not split.
        Assert.Equal(0, result[0].Slices[0].ShapedRunIndex);
        Assert.Equal(0, result[0].Slices[0].GlyphStart);
        Assert.Equal(5, result[0].Slices[0].GlyphLength);
    }

    [Fact]
    public void PreprocessTextRunsPerRun_observes_pre_cancelled_token()
    {
        // Per cycle 3d sub-cycle 1 review Rec #4 — pre-cancelled
        // tokens fast-path out before any allocation/walk.
        var runs = new List<TextRun>
        {
            new("AAA", MakeStyle()),
            new("BBB", MakeStyle()),
        };
        var modes = new[] { WhiteSpace.Normal, WhiteSpace.Pre };
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            LineBuilder.PreprocessTextRunsPerRun(runs, modes, cts.Token));
    }

    [Fact]
    public void LayoutPerRun_observes_pre_cancelled_token_in_per_run_mode()
    {
        // Per cycle 3d sub-cycle 1 review Rec #4 — LayoutPerRun must
        // propagate the cancellation token to the per-run preprocessor
        // when WhiteSpace varies. A pre-cancelled token should throw
        // before shaping fires.
        using var resolver = new TestShaperResolver();
        var sNormal = MakeStyle();
        var sPre = ComputedStyle.RentForExclusiveTesting();
        sPre.Set(PropertyId.WhiteSpace, ComputedSlot.FromKeyword(1)); // pre
        var sourceRuns = new List<TextRun>
        {
            new("AAA", sNormal),
            new("BBB", sPre), // mixed → triggers per-run preprocessor
        };
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            InlineLayouter.LayoutPerRun(sourceRuns, 100, resolver,
                LatnScript, EnLang,
                cancellationToken: cts.Token));
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
