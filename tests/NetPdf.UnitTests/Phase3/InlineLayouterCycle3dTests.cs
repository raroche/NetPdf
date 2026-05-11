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
        Assert.Single(result.Lines);
        Assert.Equal(2, result.Lines[0].Slices.Length);
        // Slice 0: SR0 [0..4) — Normal "AAA " (4 glyphs, trailing SP
        // not stripped because not last run).
        Assert.Equal(0, result.Lines[0].Slices[0].ShapedRunIndex);
        Assert.Equal(0, result.Lines[0].Slices[0].GlyphStart);
        Assert.Equal(4, result.Lines[0].Slices[0].GlyphLength);
        // Slice 1: SR1 [0..4) — Pre " BBB" (4 glyphs, leading SP
        // preserved).
        Assert.Equal(1, result.Lines[0].Slices[1].ShapedRunIndex);
        Assert.Equal(0, result.Lines[0].Slices[1].GlyphStart);
        Assert.Equal(4, result.Lines[0].Slices[1].GlyphLength);
        // Total advance: "AAA " = 3×6 + 7.2 = 25.2; " BBB" = 7.2 + 18 = 25.2.
        Assert.Equal(2 * (3 * LetterAdvance + SpaceAdvance),
            result.Lines[0].TotalAdvance, precision: 4);
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

        Assert.Single(result.Lines);
        Assert.Equal(2, result.Lines[0].Slices.Length);
        Assert.Equal(0, result.Lines[0].Slices[0].ShapedRunIndex);
        Assert.Equal(3, result.Lines[0].Slices[0].GlyphLength); // "AAA"
        Assert.Equal(1, result.Lines[0].Slices[1].ShapedRunIndex);
        Assert.Equal(4, result.Lines[0].Slices[1].GlyphLength); // " BBB"
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

        Assert.Single(result.Lines);
        Assert.Equal(2, result.Lines[0].Slices.Length);
        Assert.Equal(4, result.Lines[0].Slices[0].GlyphLength); // "AAA "
        Assert.Equal(3, result.Lines[0].Slices[1].GlyphLength); // "BBB"
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

        Assert.Single(result.Lines);
        Assert.Equal(2, result.Lines[0].Slices.Length);
        Assert.Equal(3, result.Lines[0].Slices[0].GlyphLength); // "AAA"
        Assert.Equal(4, result.Lines[0].Slices[1].GlyphLength); // " BBB" preserved
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

        Assert.Single(result.Lines);
        Assert.Equal(2, result.Lines[0].Slices.Length);
        // Slice 0: Pre "AA  " → 4 glyphs (preserved).
        Assert.Equal(0, result.Lines[0].Slices[0].ShapedRunIndex);
        Assert.Equal(4, result.Lines[0].Slices[0].GlyphLength);
        // Slice 1: Normal "  BB" → collapsed to " BB" → 3 glyphs.
        Assert.Equal(1, result.Lines[0].Slices[1].ShapedRunIndex);
        Assert.Equal(3, result.Lines[0].Slices[1].GlyphLength);
    }

    [Fact]
    public void LayoutPerRun_mixed_word_break_now_handled_per_glyph()
    {
        // Cycle 3d sub-cycle 3 broadens the matrix to WordBreak
        // mismatches via per-glyph BreakAll plumbing through
        // LineBuilder.Wrap's `inlineTextPolicyPerRun` parameter.
        // Glyphs in Normal/KeepAll source runs retain their UAX #14
        // classifications; glyphs in BreakAll source runs get the
        // Prohibited→Allowed upgrade at grapheme boundaries.
        using var resolver = new TestShaperResolver();
        var sNormal = MakeStyle();
        var sBreakAll = ComputedStyle.RentForExclusiveTesting();
        sBreakAll.Set(PropertyId.WordBreak, ComputedSlot.FromKeyword(1)); // break-all
        var sourceRuns = new List<TextRun>
        {
            new("AAA", sNormal),
            new("BBB", sBreakAll),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns, 100, resolver,
            LatnScript, EnLang);
        Assert.NotEmpty(result.Lines);
    }

    [Fact]
    public void LayoutPerRun_mixed_hyphens_now_handled_per_run()
    {
        // Cycle 3d sub-cycle 4 — Hyphens mismatch is now handled
        // via per-source-run plumbing through the hyphenation
        // pipeline (soft-hyphen pass + Liang application per-word
        // gated by source-run Hyphens).
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
        Assert.NotEmpty(result.Lines);
    }

    // --- Cycle 3d sub-cycle 4: per-source-run hyphens tests ------

    [Fact]
    public void LayoutPerRun_soft_hyphen_in_None_run_is_NOT_a_wrap_candidate()
    {
        // Per Phase 3 Task 10 cycle 3d sub-cycle 4 — when a soft
        // hyphen sits in a source run with Hyphens=None, it is NOT a
        // wrap candidate even though UAX #14 LB10 would normally
        // classify U+00AD as Allowed.
        //
        // Source: Auto "AAAA" + None "BBB­BBB" (soft hyphen in None
        // run). Budget = 30.
        // - Run 0 (Auto): no soft hyphens, Liang on "AAAA" is a
        //   no-op (synthetic font, no real patterns).
        // - Run 1 (None): the U+00AD at position 7 must be DEMOTED
        //   (breaks[7]: Allowed→Prohibited) AND not added to
        //   hyphenationAfter. Wrap doesn't snap at it.
        using var resolver = new TestShaperResolver();
        var sAuto = ComputedStyle.RentForExclusiveTesting();
        sAuto.Set(PropertyId.Hyphens, ComputedSlot.FromKeyword(2)); // auto
        var sNone = ComputedStyle.RentForExclusiveTesting();
        sNone.Set(PropertyId.Hyphens, ComputedSlot.FromKeyword(0)); // none
        var sourceRuns = new List<TextRun>
        {
            new("AAAA", sAuto),
            new("BBB­BBB", sNone),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 30, resolver, LatnScript, EnLang);

        // Run 1's soft-hyphen is not a wrap candidate → no
        // hyphenation break inside Run 1. Wrap behaves like the
        // uniform Hyphens=None case for Run 1: 7 glyphs of Run 1
        // (3 B + 1 SH-zero-advance + 3 B = 36 px) overflow as one
        // line (no internal candidates).
        Assert.True(result.Lines.Length >= 1);
        // No line ends with a hyphenation break (the soft hyphen in
        // the None run was demoted).
        foreach (var line in result.Lines)
        {
            Assert.False(line.EndsWithHyphenationBreak,
                "No line should end with a hyphenation break — the SH " +
                "is in a Hyphens:None source run, so it was demoted.");
        }
    }

    [Fact]
    public void LayoutPerRun_soft_hyphen_in_Manual_run_IS_a_wrap_candidate()
    {
        // Per Phase 3 Task 10 cycle 3d sub-cycle 4 — when a soft
        // hyphen sits in a source run with Hyphens=Manual (or Auto),
        // it is a wrap candidate as usual.
        //
        // Source: None "AAAA" + Manual "BBB­BBB". Budget = 25.
        // - Run 1's U+00AD position is NOT demoted (Hyphens=Manual).
        //   Soft-hyphen pass adds it to hyphenationAfter.
        // - Wrap should snap at the soft-hyphen position when Run 1's
        //   B-sequence overflows.
        using var resolver = new TestShaperResolver();
        var sNone = ComputedStyle.RentForExclusiveTesting();
        sNone.Set(PropertyId.Hyphens, ComputedSlot.FromKeyword(0)); // none
        var sManual = MakeStyle(); // default Manual
        var sourceRuns = new List<TextRun>
        {
            new("AAAA", sNone),
            new("BBB­BBB", sManual),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 25, resolver, LatnScript, EnLang);

        // Expect at least one line to end with a hyphenation break
        // (the SH in the Manual run was honored).
        var anyHyphenationBreak = false;
        foreach (var line in result.Lines)
        {
            if (line.EndsWithHyphenationBreak)
            {
                anyHyphenationBreak = true;
                break;
            }
        }
        Assert.True(anyHyphenationBreak,
            "At least one line should end with a hyphenation break — " +
            "the SH in the Hyphens:Manual source run is a valid wrap " +
            "candidate.");
    }

    [Fact]
    public void LayoutPerRun_uniform_Hyphens_Auto_still_works_via_global_path()
    {
        // Regression guard — when ALL runs share Hyphens=Auto (no
        // mismatch), LayoutPerRun stays on the uniform-policy
        // delegation path (no per-run array built). Liang applies
        // globally as before.
        using var resolver = new TestShaperResolver();
        var sAuto = ComputedStyle.RentForExclusiveTesting();
        sAuto.Set(PropertyId.Hyphens, ComputedSlot.FromKeyword(2)); // auto
        var sourceRuns = new List<TextRun>
        {
            new("hyphenation", sAuto),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 30, resolver, LatnScript, EnLang);

        // Auto + Liang patterns split "hyphenation".
        Assert.True(result.Lines.Length >= 2,
            $"Uniform Auto should split 'hyphenation' via Liang; got {result.Lines.Length} lines.");
    }

    [Fact]
    public void Wrap_direct_InlineTextPolicy_array_Hyphens_None_demotes_soft_hyphen()
    {
        // Direct LineBuilder.Wrap call — per-run Hyphens.None should
        // demote any soft-hyphen breaks from Allowed to Prohibited.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AAA­AAA", MakeStyle()), // soft hyphen between letters
        };
        var itemized = LineBuilder.Itemize(sourceRuns,
            NetPdf.Text.Bidi.ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver,
            LatnScript, EnLang);

        // Per-run Hyphens.None on the single run.
        var perRun = new[]
        {
            new InlineTextPolicy(WhiteSpace.Normal,
                OverflowWrap.Normal, WordBreak.Normal, Hyphens.None),
        };
        var lines = LineBuilder.Wrap(sourceRuns, shaped, 25,
            hyphens: Hyphens.None,
            inlineTextPolicyPerRun: perRun);

        // No line should end at the soft-hyphen (it was demoted).
        foreach (var line in lines)
        {
            Assert.False(line.EndsWithHyphenationBreak);
        }
    }

    // --- Cycle 3d sub-cycle 4 review hardening tests --------------

    [Fact]
    public void LayoutPerRun_Liang_breaks_inside_None_segment_of_mixed_word_are_suppressed()
    {
        // Per sub-cycle 4 review Finding #1 — when a word spans
        // two source runs Auto + None, Liang break positions
        // landing INSIDE the None segment must NOT be recorded.
        //
        // Source: Auto "hy" + None "phenation" — concat
        // "hyphenation" (11 chars), but positions 0-1 are in Auto
        // run and 2-10 in None run. Liang on "hyphenation" finds
        // positions like "hy-phen-ation" (at concat indices 1, 5).
        // Per Finding #1, only position 1 should be kept (in Auto
        // run); position 5 should be suppressed (in None run).
        //
        // Pre-fix: both positions kept (first-letter is Auto so
        // word is "in"). Post-fix: position 5 dropped.
        using var resolver = new TestShaperResolver();
        var sAuto = ComputedStyle.RentForExclusiveTesting();
        sAuto.Set(PropertyId.Hyphens, ComputedSlot.FromKeyword(2)); // auto
        var sNone = ComputedStyle.RentForExclusiveTesting();
        sNone.Set(PropertyId.Hyphens, ComputedSlot.FromKeyword(0)); // none
        var sourceRuns = new List<TextRun>
        {
            new("hy", sAuto),
            new("phenation", sNone),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 30, resolver, LatnScript, EnLang);

        // Available 30 fits ~4 chars (4×7.2 = 28.8). Only the
        // position-1 Liang break (in Auto run) is a valid candidate;
        // line 0 ends at the hy-phen split (drawable = "hy",
        // EndsWithHyphenationBreak=true). Position 5 was suppressed
        // by Finding #1's per-position gate — without it, line
        // geometry would differ.
        Assert.True(result.Lines.Length >= 2);
        // Line 0 ends with a hyphenation break (the Auto-run Liang
        // position is honored).
        Assert.True(result.Lines[0].EndsWithHyphenationBreak,
            "Line 0 should end with a hyphenation break at the " +
            "Auto-run Liang position.");
        // Line 0's drawable contains the first 2 glyphs ("hy") from
        // Run 0 — pinning the geometry to prove Finding #1 didn't
        // suppress the in-Auto break.
        Assert.Single(result.Lines[0].Slices);
        Assert.Equal(0, result.Lines[0].Slices[0].ShapedRunIndex);
        Assert.Equal(0, result.Lines[0].Slices[0].GlyphStart);
        Assert.Equal(2, result.Lines[0].Slices[0].GlyphLength);
    }

    [Fact]
    public void LayoutPerRun_Liang_runs_when_only_later_letters_are_Auto()
    {
        // Per sub-cycle 4 review Finding #1 — inverse of the test
        // above: a word starting in None + ending in Auto must
        // STILL apply Liang where the Auto segment lies. Pre-fix
        // (first-letter-only gate): word skipped entirely because
        // first letter is in None. Post-fix: Liang runs because
        // SOME letter in the word is Auto, and positions are then
        // gated per-position.
        //
        // Source: None "hy" + Auto "phenation" — Liang positions
        // at 1 and 5. Position 1 in None → suppressed. Position 5
        // in Auto → kept.
        using var resolver = new TestShaperResolver();
        var sNone = ComputedStyle.RentForExclusiveTesting();
        sNone.Set(PropertyId.Hyphens, ComputedSlot.FromKeyword(0)); // none
        var sAuto = ComputedStyle.RentForExclusiveTesting();
        sAuto.Set(PropertyId.Hyphens, ComputedSlot.FromKeyword(2)); // auto
        var sourceRuns = new List<TextRun>
        {
            new("hy", sNone),
            new("phenation", sAuto),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 50, resolver, LatnScript, EnLang);

        // Available 50 fits ~6 chars (6×7.2 = 43.2 < 50, 7×7.2 =
        // 50.4 > 50). Position-1 Liang in None-run is suppressed.
        // Position-5 (after "phen", concat pos 5) in Auto-run is
        // honored — wrap snaps there. Line 0 = "hyphen" (6 chars).
        Assert.True(result.Lines.Length >= 2);
        Assert.True(result.Lines[0].EndsWithHyphenationBreak,
            "Line 0 should end at the in-Auto Liang break " +
            "(position 5 = end of 'hyphen').");
        // Line 0 contains 6 glyphs across 2 slices (Run 0: 2 'h'+'y',
        // Run 1: 4 'phen').
        var line0Glyphs = 0;
        foreach (var s in result.Lines[0].Slices) line0Glyphs += s.GlyphLength;
        Assert.Equal(6, line0Glyphs);
    }

    [Fact]
    public void LayoutPerRun_disabled_soft_hyphen_does_NOT_suppress_Liang_in_Auto_segment()
    {
        // Per sub-cycle 4 review Finding #2 — a U+00AD in a
        // Hyphens=None source run is disabled (demoted), so it
        // must NOT suppress Liang opportunities elsewhere in the
        // same tokenized word that are in Auto source runs.
        //
        // Source: Auto "hy" + None "­" + Auto "phenation" —
        // concat "hy­phenation" (12 chars). The U+00AD at
        // concat pos 2 is in a None run → demoted. Pre-fix
        // (whole-word SH suppression): Liang would be skipped
        // entirely. Post-fix: Liang runs (the SH is disabled +
        // ignored for the suppression rule).
        using var resolver = new TestShaperResolver();
        var sAuto = ComputedStyle.RentForExclusiveTesting();
        sAuto.Set(PropertyId.Hyphens, ComputedSlot.FromKeyword(2)); // auto
        var sNone = ComputedStyle.RentForExclusiveTesting();
        sNone.Set(PropertyId.Hyphens, ComputedSlot.FromKeyword(0)); // none
        var sourceRuns = new List<TextRun>
        {
            new("hy", sAuto),
            new("­", sNone),
            new("phenation", sAuto),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 50, resolver, LatnScript, EnLang);

        // Liang on "hyphenation" still produces position-5 break
        // (after "phen", concat pos 6 with the SH inserted between
        // hy and phenation: 'h''y'SH'p''h''e''n''a''t''i''o''n').
        // Auto runs cover positions 0-1 (hy) + 3-11 (phenation).
        // Pre-fix: SH would suppress all Liang. Post-fix: Liang
        // applies and finds breaks in the Auto segments.
        Assert.True(result.Lines.Length >= 2,
            $"Expected ≥ 2 lines (Auto Liang break in the longer " +
            $"segment should fire); got {result.Lines.Length}. Pre-fix " +
            $"would yield 1 overflowing line because the disabled " +
            $"SH suppressed all Liang.");
    }

    [Fact]
    public void LayoutPerRun_Manual_run_alongside_Auto_does_NOT_get_Liang_inside_Manual()
    {
        // Per sub-cycle 4 review Finding #1 — Manual + Auto mix.
        // Manual source runs honor soft-hyphens but NOT Liang.
        // Per-position gate: positions in Manual segment are
        // dropped from Liang output.
        //
        // Source: Manual "hy" + Auto "phenation". Liang positions:
        // 1 (Manual) suppressed, 5 (Auto) kept.
        using var resolver = new TestShaperResolver();
        var sManual = MakeStyle(); // default Manual
        var sAuto = ComputedStyle.RentForExclusiveTesting();
        sAuto.Set(PropertyId.Hyphens, ComputedSlot.FromKeyword(2)); // auto
        var sourceRuns = new List<TextRun>
        {
            new("hy", sManual),
            new("phenation", sAuto),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 30, resolver, LatnScript, EnLang);

        // The position-1 Liang break in the Manual segment is
        // suppressed. Position 5 in Auto is kept but doesn't
        // satisfy budget 30 (would need 6 chars × 7.2 = 43.2 px
        // line). So wrap may fire later or overflow.
        Assert.True(result.Lines.Length >= 1);
        // Line 0 should not be just "hy" (2 glyphs) — that would
        // mean the Manual segment's Liang fired, which is wrong.
        var line0Glyphs = 0;
        foreach (var s in result.Lines[0].Slices) line0Glyphs += s.GlyphLength;
        Assert.True(line0Glyphs != 2 || !result.Lines[0].EndsWithHyphenationBreak,
            $"Line 0 = 2 glyphs + EndsWithHyphenationBreak would " +
            $"mean the Manual segment's Liang fired (wrong). " +
            $"Got {line0Glyphs} glyphs, EndsWithHyphenationBreak={result.Lines[0].EndsWithHyphenationBreak}.");
    }

    // --- Cycle 3d sub-cycle 3: per-glyph word-break tests -------

    [Fact]
    public void LayoutPerRun_BreakAll_run_splits_at_every_glyph_while_Normal_run_stays_intact()
    {
        // Per Phase 3 Task 10 cycle 3d sub-cycle 3 — per-source-run
        // WordBreak. Source: Normal(word-break:normal) "AAA " (with
        // trailing SP for the run-boundary wrap opportunity) +
        // Normal(word-break:break-all) "BBBB". Budget = 18.
        //
        // Geometry:
        // - "AAA " = 3 letters (18 px) + SP (7.2 px) = 25.2 px.
        // - "BBBB" = 4 letters × 6 = 24 px.
        // - Concat: "AAA BBBB" (8 chars / 8 glyphs).
        //
        // Wrap trace under budget 18:
        // 1. cursors 0-2 'AAA': cum 6/12/18 (fits exactly).
        // 2. cursor 3 SP (Normal): cum 25.2 > 18. opp=Allowed (UAX
        //    #14 LB18 after SP). lastAllowed=3.
        // 3. cursor 4 'B' (Run 1): cum 31.2 > 18. Snap to
        //    lastAllowed=3. Trim trailing IsBreakSpace (SP at
        //    glyph 3 is collapsible-Normal break-space). drawableEnd=2.
        //    Emit line 0 = [0..2] = 3 glyphs "AAA" (Run 0 intact).
        //    lineStart=4.
        // 4. Run 1 (BreakAll) glyphs get per-glyph upgrade: each
        //    Prohibited boundary at grapheme boundary becomes
        //    Allowed. Wraps at every 3 glyphs (3×6=18 fits, 4×6=24
        //    overflows): line 1 = "BBB", line 2 = "B" (mandatory).
        using var resolver = new TestShaperResolver();
        var sNormal = MakeStyle();
        var sBreakAll = ComputedStyle.RentForExclusiveTesting();
        sBreakAll.Set(PropertyId.WordBreak, ComputedSlot.FromKeyword(1)); // break-all
        var sourceRuns = new List<TextRun>
        {
            new("AAA ", sNormal),
            new("BBBB", sBreakAll),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 18, resolver, LatnScript, EnLang);

        // 3 lines: Run 0 stays whole, Run 1 splits per BreakAll.
        Assert.Equal(3, result.Lines.Length);

        // Line 0: 3 'A' glyphs from Run 0 (trailing SP trimmed at
        // wrap boundary per Normal collapse).
        Assert.Single(result.Lines[0].Slices);
        Assert.Equal(0, result.Lines[0].Slices[0].ShapedRunIndex);
        Assert.Equal(0, result.Lines[0].Slices[0].GlyphStart);
        Assert.Equal(3, result.Lines[0].Slices[0].GlyphLength);

        // Line 1: 3 'B' glyphs from Run 1 (BreakAll wrap at glyph 3).
        Assert.Single(result.Lines[1].Slices);
        Assert.Equal(1, result.Lines[1].Slices[0].ShapedRunIndex);
        Assert.Equal(0, result.Lines[1].Slices[0].GlyphStart);
        Assert.Equal(3, result.Lines[1].Slices[0].GlyphLength);

        // Line 2: last 'B' glyph from Run 1 (mandatory at end).
        Assert.Single(result.Lines[2].Slices);
        Assert.Equal(1, result.Lines[2].Slices[0].ShapedRunIndex);
        Assert.Equal(3, result.Lines[2].Slices[0].GlyphStart);
        Assert.Equal(1, result.Lines[2].Slices[0].GlyphLength);
        Assert.True(result.Lines[2].EndsWithMandatoryBreak);
    }

    [Fact]
    public void LayoutPerRun_uniform_BreakAll_still_works_via_global_path()
    {
        // Regression guard — when ALL runs share word-break:break-all
        // (no mismatch), LayoutPerRun stays on the uniform-policy
        // delegation path (no per-run array built). The existing
        // cycle 3b BreakAll behavior continues to apply.
        using var resolver = new TestShaperResolver();
        var sBreakAll = ComputedStyle.RentForExclusiveTesting();
        sBreakAll.Set(PropertyId.WordBreak, ComputedSlot.FromKeyword(1)); // break-all
        var sourceRuns = new List<TextRun>
        {
            new("AAAA", sBreakAll),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 12, resolver, LatnScript, EnLang);

        // BreakAll lets every glyph boundary be a soft break →
        // 2 glyphs (12 px) fits → wrap at glyph 2.
        Assert.Equal(2, result.Lines.Length);
    }

    // --- Cycle 3d sub-cycle 3 review hardening tests --------------

    [Fact]
    public void LayoutPerRun_BreakAll_first_then_Normal_no_whitespace_boundary()
    {
        // Per sub-cycle 3 review Finding #3 — cross-run BreakAll
        // boundary uses "either side may opt in". Source:
        // BreakAll "AAAA" + Normal "BBBB" with NO whitespace
        // between. Budget = 12.
        //
        // - Run 0 (BreakAll): glyph-pair breaks upgraded.
        //   cursors 0-1: cum 6/12. opp upgraded to Allowed.
        //   cursor 2: cum 18 > 12. Snap to lastAllowed=1. Emit
        //   [0..1] = "AA". lineStart=2.
        // - cursor 2 'A' (Run 0): cum 6. lastAllowed upgrade fires.
        // - cursor 3 'A' (last of Run 0, BreakAll): cum 12.
        //   At the run boundary (next shaped run is Run 1 = Normal),
        //   Finding #3's cross-run "either side opts in" gives this
        //   glyph's BreakAll its upgrade naturally (same run, BreakAll).
        // - cursor 4 'B' (Run 1, Normal): cum 18 > 12. Snap to
        //   lastAllowed=3. Emit [2..3] = "AA". lineStart=4.
        // - Run 1 (Normal): no candidates → overflows as a line.
        using var resolver = new TestShaperResolver();
        var sBreakAll = ComputedStyle.RentForExclusiveTesting();
        sBreakAll.Set(PropertyId.WordBreak, ComputedSlot.FromKeyword(1)); // break-all
        var sNormal = MakeStyle();
        var sourceRuns = new List<TextRun>
        {
            new("AAAA", sBreakAll),
            new("BBBB", sNormal),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 12, resolver, LatnScript, EnLang);

        // Run 0 splits per BreakAll; Run 1 stays whole overflowing.
        Assert.True(result.Lines.Length >= 3,
            $"Expected at least 3 lines (Run 0 splits, Run 1 overflows); got {result.Lines.Length}.");
        // Last line is Run 1's full "BBBB" (no internal BreakAll).
        var lastLine = result.Lines[result.Lines.Length - 1];
        Assert.True(lastLine.EndsWithMandatoryBreak);
        Assert.Single(lastLine.Slices);
        Assert.Equal(1, lastLine.Slices[0].ShapedRunIndex);
        Assert.Equal(4, lastLine.Slices[0].GlyphLength);
    }

    [Fact]
    public void LayoutPerRun_Normal_immediately_followed_by_BreakAll_no_whitespace_either_side_opts_in()
    {
        // Per sub-cycle 3 review Finding #3 — Normal "AAAA" followed
        // by BreakAll "BBBB" with NO whitespace between. Budget = 18.
        //
        // The break BEFORE the first BreakAll glyph (= AT the run
        // boundary between glyph 3 [last A] and glyph 4 [first B])
        // is upgraded via Finding #3's cross-run "either side opts
        // in" rule. Glyph 3's perGlyphBreakAll is set TRUE because
        // it's the LAST glyph of its shaped run AND the next shaped
        // run is in a BreakAll source run. So cursor 4's overflow
        // can snap back to lastAllowed=3.
        using var resolver = new TestShaperResolver();
        var sNormal = MakeStyle();
        var sBreakAll = ComputedStyle.RentForExclusiveTesting();
        sBreakAll.Set(PropertyId.WordBreak, ComputedSlot.FromKeyword(1)); // break-all
        var sourceRuns = new List<TextRun>
        {
            new("AAAA", sNormal),
            new("BBBB", sBreakAll),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 18, resolver, LatnScript, EnLang);

        // Expected with Finding #3 fix:
        // - Line 0: Run 0 "AAAA" (4 glyphs, 24 px) — wait, 24 > 18
        //   so would need a wrap candidate inside Run 0 OR at its
        //   END. With Finding #3, the LAST glyph of Run 0 gets
        //   its break upgraded (Run 1 is BreakAll). At cursor 4
        //   (first B, cum 30 > 18), snap to lastAllowed=3.
        //   Emit [0..3] = "AAAA" (4 glyphs). lineStart=4.
        // - Run 1 (BreakAll) splits per its own upgrades.
        //   3 letters (18 px) fits; 4 overflows. Split at glyph 6.
        //   Emit [4..6] = "BBB". Then [7..7] = "B" mandatory.
        Assert.Equal(3, result.Lines.Length);

        // Line 0: full Run 0 "AAAA" (4 glyphs).
        Assert.Single(result.Lines[0].Slices);
        Assert.Equal(0, result.Lines[0].Slices[0].ShapedRunIndex);
        Assert.Equal(0, result.Lines[0].Slices[0].GlyphStart);
        Assert.Equal(4, result.Lines[0].Slices[0].GlyphLength);

        // Line 1: Run 1 first 3 'B's.
        Assert.Single(result.Lines[1].Slices);
        Assert.Equal(1, result.Lines[1].Slices[0].ShapedRunIndex);
        Assert.Equal(0, result.Lines[1].Slices[0].GlyphStart);
        Assert.Equal(3, result.Lines[1].Slices[0].GlyphLength);

        // Line 2: Run 1 last 'B', mandatory.
        Assert.Single(result.Lines[2].Slices);
        Assert.Equal(1, result.Lines[2].Slices[0].ShapedRunIndex);
        Assert.Equal(3, result.Lines[2].Slices[0].GlyphStart);
        Assert.Equal(1, result.Lines[2].Slices[0].GlyphLength);
        Assert.True(result.Lines[2].EndsWithMandatoryBreak);
    }

    [Fact]
    public void LayoutPerRun_NoWrap_BreakAll_interaction_NoWrap_wins()
    {
        // Per sub-cycle 3 review Finding #4 — interaction test:
        // a source run with WhiteSpace=NoWrap AND WordBreak=BreakAll
        // must behave like NoWrap (no breaks). NoWrap downgrade in
        // the wrap loop runs AFTER the BreakAll upgrade in the same
        // pass; the downgrade restores Prohibited at all positions.
        //
        // To trigger the interaction we need a per-run policy mix.
        // Use NoWrap+BreakAll for Run 0, Normal+Normal for Run 1
        // (with a space at the boundary so wrap can fire there).
        using var resolver = new TestShaperResolver();
        var sNoWrapBreakAll = ComputedStyle.RentForExclusiveTesting();
        sNoWrapBreakAll.Set(PropertyId.WhiteSpace, ComputedSlot.FromKeyword(2)); // nowrap
        sNoWrapBreakAll.Set(PropertyId.WordBreak, ComputedSlot.FromKeyword(1)); // break-all
        var sNormal = MakeStyle();
        var sourceRuns = new List<TextRun>
        {
            new("AAAAAAAA", sNoWrapBreakAll), // 8 letters
            new(" BBB", sNormal),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 12, resolver, LatnScript, EnLang);

        // NoWrap suppresses all soft breaks inside Run 0 even though
        // WordBreak=BreakAll would have upgraded them. Run 0 stays
        // as one overflowing line; Run 1 wraps separately (the SP
        // at glyph 8 provides a wrap candidate).
        Assert.True(result.Lines.Length >= 2);
        // Line 0 contains all 8 'A' glyphs from Run 0 — NoWrap
        // semantics + the cross-run wrap at the SP boundary.
        Assert.True(result.Lines[0].Slices.Length >= 1);
        Assert.Equal(0, result.Lines[0].Slices[0].ShapedRunIndex);
        Assert.Equal(0, result.Lines[0].Slices[0].GlyphStart);
        Assert.Equal(8, result.Lines[0].Slices[0].GlyphLength);
    }

    [Fact]
    public void LayoutPerRun_KeepAll_mixed_with_Normal_throws_pending_CJK_support()
    {
        // Per sub-cycle 3 review Finding #1 — KeepAll on mismatch
        // throws because the wrap pass currently doesn't implement
        // KeepAll semantics (CJK inter-character break suppression
        // requires UAX #24 script detection + LB30b handling).
        using var resolver = new TestShaperResolver();
        var sNormal = MakeStyle();
        var sKeepAll = ComputedStyle.RentForExclusiveTesting();
        sKeepAll.Set(PropertyId.WordBreak, ComputedSlot.FromKeyword(2)); // keep-all
        var sourceRuns = new List<TextRun>
        {
            new("AAA", sNormal),
            new("BBB", sKeepAll),
        };
        var ex = Assert.Throws<NotSupportedException>(() =>
            InlineLayouter.LayoutPerRun(sourceRuns, 100, resolver,
                LatnScript, EnLang));
        Assert.Contains("keep-all", ex.Message);
    }

    [Fact]
    public void Wrap_inlineTextPolicyPerRun_invalid_WordBreak_throws()
    {
        // Per sub-cycle 3 review Finding #4 — invalid WordBreak
        // enum values in the per-run array throw at entry.
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

        var perRun = new[]
        {
            InlineTextPolicy.Default,
            new InlineTextPolicy(WhiteSpace.Normal,
                OverflowWrap.Normal, (WordBreak)99, Hyphens.Manual),
        };

        var ex = Assert.Throws<ArgumentException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, 100,
                inlineTextPolicyPerRun: perRun));
        Assert.Contains("inlineTextPolicyPerRun[1].WordBreak", ex.Message);
    }

    [Fact]
    public void Wrap_inlineTextPolicyPerRun_invalid_Hyphens_throws()
    {
        // Per sub-cycle 3 review Finding #4 — invalid Hyphens
        // enum values in the per-run array throw at entry.
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

        var perRun = new[]
        {
            InlineTextPolicy.Default,
            new InlineTextPolicy(WhiteSpace.Normal,
                OverflowWrap.Normal, WordBreak.Normal, (Hyphens)99),
        };

        var ex = Assert.Throws<ArgumentException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, 100,
                inlineTextPolicyPerRun: perRun));
        Assert.Contains("inlineTextPolicyPerRun[1].Hyphens", ex.Message);
    }

    [Fact]
    public void Wrap_inlineTextPolicyPerRun_Hyphens_now_supports_per_run_mismatch()
    {
        // Cycle 3d sub-cycle 4 — per-run Hyphens is now plumbed
        // through the hyphenation pipeline. The sub-cycle 3 hardening
        // guard (per-run Hyphens must equal global) is removed; the
        // hyphenationAfter pipeline gates per-position decisions
        // based on the source run's Hyphens.
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

        var perRun = new[]
        {
            new InlineTextPolicy(WhiteSpace.Normal,
                OverflowWrap.Normal, WordBreak.Normal, Hyphens.Manual),
            new InlineTextPolicy(WhiteSpace.Normal,
                OverflowWrap.Normal, WordBreak.Normal, Hyphens.Auto),
        };

        // Should NOT throw — the per-run Hyphens mismatch is now
        // accepted. The result is a normal wrap with the per-run
        // Hyphens decisions applied internally.
        var lines = LineBuilder.Wrap(sourceRuns, shaped, 100,
            hyphens: Hyphens.Manual,
            inlineTextPolicyPerRun: perRun);
        Assert.NotEmpty(lines);
    }

    [Fact]
    public void Wrap_direct_InlineTextPolicy_array_BreakAll_per_run_splits_correctly()
    {
        // Per sub-cycle 3 review Finding #4 — direct LineBuilder.Wrap
        // call with inlineTextPolicyPerRun array. Pins the per-glyph
        // BreakAll upgrade behavior end-to-end without going through
        // LayoutPerRun's bookkeeping.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AAA ", MakeStyle()),
            new("BBBB", MakeStyle()),
        };
        var itemized = LineBuilder.Itemize(sourceRuns,
            NetPdf.Text.Bidi.ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver,
            LatnScript, EnLang);

        // Per-run policies: Run 0 = Normal; Run 1 = BreakAll.
        var perRun = new[]
        {
            new InlineTextPolicy(WhiteSpace.Normal,
                OverflowWrap.Normal, WordBreak.Normal, Hyphens.Manual),
            new InlineTextPolicy(WhiteSpace.Normal,
                OverflowWrap.Normal, WordBreak.BreakAll, Hyphens.Manual),
        };
        var preprocessed = LineBuilder.PreprocessTextRuns(sourceRuns,
            WhiteSpace.Normal);
        var preprocessedItemized = LineBuilder.Itemize(preprocessed,
            NetPdf.Text.Bidi.ParagraphDirection.LeftToRight);
        var preprocessedShaped = LineBuilder.Shape(preprocessed,
            preprocessedItemized, resolver, LatnScript, EnLang);

        var lines = LineBuilder.Wrap(preprocessed, preprocessedShaped, 18,
            wordBreak: WordBreak.Normal,
            inlineTextPolicyPerRun: perRun);

        // Same geometry as the integration test:
        // Line 0: 3 glyphs "AAA" (Run 0).
        // Line 1: 3 glyphs "BBB" (Run 1, BreakAll).
        // Line 2: 1 glyph "B" (Run 1, mandatory).
        Assert.Equal(3, lines.Length);
        Assert.Equal(0, lines[0].Slices[0].ShapedRunIndex);
        Assert.Equal(3, lines[0].Slices[0].GlyphLength);
        Assert.Equal(1, lines[1].Slices[0].ShapedRunIndex);
        Assert.Equal(3, lines[1].Slices[0].GlyphLength);
        Assert.Equal(1, lines[2].Slices[0].ShapedRunIndex);
        Assert.Equal(1, lines[2].Slices[0].GlyphLength);
    }

    // --- Cycle 3d sub-cycle 2: per-glyph overflow-wrap tests -----

    [Fact]
    public void LayoutPerRun_anywhere_in_one_run_does_NOT_break_inside_normal_overflow_wrap_run()
    {
        // Source: Normal(overflow-wrap:normal) "AAAA" (4 glyphs) +
        // Normal(overflow-wrap:anywhere) "BBBBB" (5 glyphs).
        // Budget = 10 (very small).
        //
        // Per cycle 3d sub-cycle 2 review Rec #3 — exact line/slice
        // geometry pinned, not just `>= 2`.
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

        // 6 total lines: Run 0 emits as one overflowing 4-glyph line
        // (no anywhere fire inside normal-overflow-wrap), then Run 1
        // splits at each glyph boundary (anywhere on every same-run
        // pair).
        Assert.Equal(6, result.Lines.Length);

        // Line 0: full Run 0 (4 'A' glyphs). 4 letters × 6.0 = 24 px
        // (overflowing 10 budget — kept together because Run 0 is not
        // overflow-wrap:anywhere).
        Assert.Single(result.Lines[0].Slices);
        Assert.Equal(0, result.Lines[0].Slices[0].ShapedRunIndex);
        Assert.Equal(0, result.Lines[0].Slices[0].GlyphStart);
        Assert.Equal(4, result.Lines[0].Slices[0].GlyphLength);
        Assert.Equal(4 * LetterAdvance, result.Lines[0].TotalAdvance, precision: 4);

        // Lines 1-5: each is a single 'B' glyph from Run 1 (anywhere
        // forced break at every glyph pair). Line 5 is the final
        // mandatory-break line.
        for (var i = 0; i < 5; i++)
        {
            var line = result.Lines[1 + i];
            Assert.Single(line.Slices);
            Assert.Equal(1, line.Slices[0].ShapedRunIndex);
            Assert.Equal(i, line.Slices[0].GlyphStart);
            Assert.Equal(1, line.Slices[0].GlyphLength);
            Assert.Equal(LetterAdvance, line.TotalAdvance, precision: 4);
        }
        Assert.False(result.Lines[0].EndsWithMandatoryBreak);
        Assert.True(result.Lines[5].EndsWithMandatoryBreak);
    }

    [Fact]
    public void LayoutPerRun_anywhere_in_one_run_DOES_break_inside_anywhere_run()
    {
        // Inverse of the test above — when the anywhere run is the
        // one being walked, the forced break DOES fire inside it.
        // Per cycle 3d sub-cycle 2 review Rec #3 — exact line/slice
        // geometry pinned.
        //
        // Source: Normal(anywhere) "AAAAA" (5 glyphs) +
        // Normal(normal overflow-wrap) "BBBB" (4 glyphs). Budget = 10.
        //
        // Lines 0..4: Run 0 splits into 5 single-glyph lines (anywhere
        // fires at every same-run pair).
        // Line 5: Run 1 (BBBB) stays whole and overflows (no anywhere
        // inside normal-overflow-wrap).
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

        Assert.Equal(6, result.Lines.Length);

        // Lines 0..4: Each a single 'A' glyph from Run 0.
        for (var i = 0; i < 5; i++)
        {
            var line = result.Lines[i];
            Assert.Single(line.Slices);
            Assert.Equal(0, line.Slices[0].ShapedRunIndex);
            Assert.Equal(i, line.Slices[0].GlyphStart);
            Assert.Equal(1, line.Slices[0].GlyphLength);
            Assert.Equal(LetterAdvance, line.TotalAdvance, precision: 4);
        }

        // Line 5: Full Run 1 "BBBB" (4 glyphs, overflowing).
        Assert.Single(result.Lines[5].Slices);
        Assert.Equal(1, result.Lines[5].Slices[0].ShapedRunIndex);
        Assert.Equal(0, result.Lines[5].Slices[0].GlyphStart);
        Assert.Equal(4, result.Lines[5].Slices[0].GlyphLength);
        Assert.Equal(4 * LetterAdvance, result.Lines[5].TotalAdvance, precision: 4);
        Assert.True(result.Lines[5].EndsWithMandatoryBreak);
    }

    // --- Cycle 3d sub-cycle 2 review hardening tests ---------------

    [Fact]
    public void LayoutPerRun_anywhere_does_NOT_split_combining_mark_cluster()
    {
        // Per cycle 3d sub-cycle 2 review Rec #1 — the
        // overflow-wrap:anywhere forced-break fallback must NOT split
        // inside a grapheme cluster. Source: "ÁÁÁ"
        // (3 clusters, each "A + COMBINING ACUTE ACCENT" forming one
        // grapheme cluster per UAX #29). With overflow-wrap:anywhere
        // + a budget that overflows mid-cluster, anywhere must snap
        // to the next grapheme boundary instead of mid-cluster.
        //
        // Geometry under synthetic font: 'A' = 6.0 px, U+0301 (.notdef) =
        // 7.2 px. Cluster advance = 13.2 px. Budget = 10 → overflows at
        // the combining mark, but the cluster A+́ must stay whole
        // (line 0 contains BOTH glyphs).
        using var resolver = new TestShaperResolver();
        var sAnywhere = ComputedStyle.RentForExclusiveTesting();
        sAnywhere.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere
        var sourceRuns = new List<TextRun>
        {
            new("ÁÁÁ", sAnywhere),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 10, resolver, LatnScript, EnLang);

        // 3 lines, each containing ONE full cluster (2 glyphs:
        // base 'A' + combining mark). Under HarfBuzz mark-positioning
        // rules the combining mark glyph has XAdvance = 0 (positioned
        // via XOffset relative to the base char), so each cluster's
        // SliceAdvance is just the base 'A' advance (6.0 px).
        Assert.Equal(3, result.Lines.Length);
        for (var i = 0; i < 3; i++)
        {
            var line = result.Lines[i];
            Assert.Single(line.Slices);
            Assert.Equal(0, line.Slices[0].ShapedRunIndex);
            Assert.Equal(i * 2, line.Slices[0].GlyphStart);
            // 2 glyphs (base + combining mark) — cluster intact.
            Assert.Equal(2, line.Slices[0].GlyphLength);
            Assert.Equal(LetterAdvance, line.TotalAdvance, precision: 4);
        }
        // Final line ends with mandatory (LB3); earlier lines end via
        // anywhere snap-back.
        Assert.False(result.Lines[0].EndsWithMandatoryBreak);
        Assert.False(result.Lines[1].EndsWithMandatoryBreak);
        Assert.True(result.Lines[2].EndsWithMandatoryBreak);
    }

    [Fact]
    public void LayoutPerRun_anywhere_does_NOT_split_at_NoWrap_Pre_boundary()
    {
        // Per cycle 3d sub-cycle 2 review Rec #2 — two adjacent
        // non-wrap-friendly source runs (NoWrap + Pre or Pre +
        // NoWrap) cannot become an anywhere break site even at their
        // style boundary. Per CSS Text L3 §5.1, overflow-wrap "only
        // has an effect when white-space allows wrapping" — and
        // neither side allows wrapping here.
        //
        // Source: NoWrap+Anywhere "AAAAA" + Pre+Anywhere "BBBBB".
        // Budget = 10. Both runs disallow wrapping by WhiteSpace; the
        // anywhere fallback's cross-run wrap-friendly gate must keep
        // the unbreakable sequence whole.
        using var resolver = new TestShaperResolver();
        var sNoWrapAnywhere = ComputedStyle.RentForExclusiveTesting();
        sNoWrapAnywhere.Set(PropertyId.WhiteSpace, ComputedSlot.FromKeyword(2)); // nowrap
        sNoWrapAnywhere.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere
        var sPreAnywhere = ComputedStyle.RentForExclusiveTesting();
        sPreAnywhere.Set(PropertyId.WhiteSpace, ComputedSlot.FromKeyword(1)); // pre
        sPreAnywhere.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere
        var sourceRuns = new List<TextRun>
        {
            new("AAAAA", sNoWrapAnywhere),
            new("BBBBB", sPreAnywhere),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 10, resolver, LatnScript, EnLang);

        // Single overflowing line — neither run allows wrapping +
        // the boundary between them is not a valid anywhere site.
        Assert.Single(result.Lines);
        Assert.Equal(2, result.Lines[0].Slices.Length);
        Assert.Equal(0, result.Lines[0].Slices[0].ShapedRunIndex);
        Assert.Equal(0, result.Lines[0].Slices[0].GlyphStart);
        Assert.Equal(5, result.Lines[0].Slices[0].GlyphLength);
        Assert.Equal(1, result.Lines[0].Slices[1].ShapedRunIndex);
        Assert.Equal(0, result.Lines[0].Slices[1].GlyphStart);
        Assert.Equal(5, result.Lines[0].Slices[1].GlyphLength);
    }

    [Fact]
    public void LayoutPerRun_anywhere_does_NOT_split_at_Pre_NoWrap_boundary()
    {
        // Symmetry of the test above — Pre then NoWrap, same result
        // (neither side wrap-friendly, no anywhere fire).
        using var resolver = new TestShaperResolver();
        var sPreAnywhere = ComputedStyle.RentForExclusiveTesting();
        sPreAnywhere.Set(PropertyId.WhiteSpace, ComputedSlot.FromKeyword(1)); // pre
        sPreAnywhere.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere
        var sNoWrapAnywhere = ComputedStyle.RentForExclusiveTesting();
        sNoWrapAnywhere.Set(PropertyId.WhiteSpace, ComputedSlot.FromKeyword(2)); // nowrap
        sNoWrapAnywhere.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere
        var sourceRuns = new List<TextRun>
        {
            new("AAAAA", sPreAnywhere),
            new("BBBBB", sNoWrapAnywhere),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 10, resolver, LatnScript, EnLang);

        Assert.Single(result.Lines);
        Assert.Equal(2, result.Lines[0].Slices.Length);
        Assert.Equal(5, result.Lines[0].Slices[0].GlyphLength);
        Assert.Equal(5, result.Lines[0].Slices[1].GlyphLength);
    }

    [Fact]
    public void LayoutPerRun_anywhere_DOES_split_at_Normal_NoWrap_boundary_when_normal_side_is_anywhere()
    {
        // Inverse of Rec #2 — when at least ONE side is wrap-friendly,
        // the cross-run boundary IS a valid anywhere site. Source:
        // Normal+Anywhere "AAAAA" + NoWrap+Anywhere "BBBBB". Budget = 10.
        // - Run 0 (Normal+Anywhere) splits per anywhere internally.
        // - At the boundary between glyph 4 (Normal) and glyph 5
        //   (NoWrap): cross-run, prev wrap-friendly OR cursor wrap-
        //   friendly → wsAllows=true. Fire at boundary.
        // - Run 1 (NoWrap+Anywhere): same-run gate fails (NoWrap not
        //   wrap-friendly) — stays as one overflowing line.
        using var resolver = new TestShaperResolver();
        var sNormalAnywhere = ComputedStyle.RentForExclusiveTesting();
        sNormalAnywhere.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere
        var sNoWrapAnywhere = ComputedStyle.RentForExclusiveTesting();
        sNoWrapAnywhere.Set(PropertyId.WhiteSpace, ComputedSlot.FromKeyword(2)); // nowrap
        sNoWrapAnywhere.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere
        var sourceRuns = new List<TextRun>
        {
            new("AAAAA", sNormalAnywhere),
            new("BBBBB", sNoWrapAnywhere),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns,
            availableInlineSize: 10, resolver, LatnScript, EnLang);

        // Run 0 splits into 5 single-glyph lines; Run 1 stays whole
        // as one overflowing line at the end.
        Assert.Equal(6, result.Lines.Length);
        for (var i = 0; i < 5; i++)
        {
            Assert.Single(result.Lines[i].Slices);
            Assert.Equal(0, result.Lines[i].Slices[0].ShapedRunIndex);
            Assert.Equal(1, result.Lines[i].Slices[0].GlyphLength);
        }
        Assert.Single(result.Lines[5].Slices);
        Assert.Equal(1, result.Lines[5].Slices[0].ShapedRunIndex);
        Assert.Equal(5, result.Lines[5].Slices[0].GlyphLength);
    }

    [Fact]
    public void Wrap_overflowWrapPerRun_length_mismatch_throws()
    {
        // Per cycle 3d sub-cycle 2 review Rec #4 refactor — the
        // overflowWrapPerRun parallel array was replaced by a single
        // inlineTextPolicyPerRun: IReadOnlyList<InlineTextPolicy>?
        // parameter that bundles WhiteSpace + OverflowWrap +
        // WordBreak + Hyphens. The OverflowWrap validation moved
        // alongside the WhiteSpace validation.
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

        var bogusPerRun = new[]
        {
            InlineTextPolicy.Default,
        }; // length 1 != 2

        Assert.Throws<ArgumentException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, 100,
                inlineTextPolicyPerRun: bogusPerRun));
    }

    [Fact]
    public void Wrap_overflowWrapPerRun_invalid_enum_value_throws()
    {
        // Per cycle 3d sub-cycle 2 review Rec #4 refactor — invalid
        // OverflowWrap enum values inside the per-run policy array
        // throw at entry.
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

        var perRun = new[]
        {
            InlineTextPolicy.Default,
            new InlineTextPolicy(WhiteSpace.Normal,
                (OverflowWrap)99, WordBreak.Normal, Hyphens.Manual),
        };

        var ex = Assert.Throws<ArgumentException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, 100,
                inlineTextPolicyPerRun: perRun));
        Assert.Contains("inlineTextPolicyPerRun[1].OverflowWrap", ex.Message);
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

        Assert.Equal(2, result.Lines.Length);
        // --- Line 0: "AAA " (4 glyphs, trailing SP preserved per
        // PreWrap semantics — Rec #1 fix).
        Assert.False(result.Lines[0].EndsWithMandatoryBreak);
        Assert.Single(result.Lines[0].Slices);
        Assert.Equal(0, result.Lines[0].Slices[0].ShapedRunIndex);
        Assert.Equal(0, result.Lines[0].Slices[0].GlyphStart);
        Assert.Equal(4, result.Lines[0].Slices[0].GlyphLength);
        // Total advance INCLUDES the preserved trailing SP.
        Assert.Equal(3 * LetterAdvance + SpaceAdvance,
            result.Lines[0].TotalAdvance, precision: 4);
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
        Assert.True(result.Lines.Length >= 1);
        Assert.Single(result.Lines[0].Slices);
        Assert.Equal(0, result.Lines[0].Slices[0].ShapedRunIndex);
        Assert.Equal(0, result.Lines[0].Slices[0].GlyphStart);
        Assert.Equal(5, result.Lines[0].Slices[0].GlyphLength);
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

        Assert.True(result.Lines.Length >= 1);
        // First slice is the entire 5-glyph Pre run, not split.
        Assert.Equal(0, result.Lines[0].Slices[0].ShapedRunIndex);
        Assert.Equal(0, result.Lines[0].Slices[0].GlyphStart);
        Assert.Equal(5, result.Lines[0].Slices[0].GlyphLength);
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
