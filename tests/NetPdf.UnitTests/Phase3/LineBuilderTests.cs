// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
using NetPdf.Layout.Inline;
using NetPdf.Text.Bidi;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>Per Phase 3 Task 9 cycle 1 — tests for
/// <see cref="LineBuilder.Itemize"/>. Cycle 1 covers bidi + style-
/// boundary itemization; shaping + line-breaking + wrapping are
/// cycle 2/3.</summary>
public class LineBuilderTests
{
    // --- Empty input ----------------------------------------------

    [Fact]
    public void Itemize_empty_input_returns_empty()
    {
        var result = LineBuilder.Itemize(
            Array.Empty<TextRun>(),
            ParagraphDirection.LeftToRight);
        Assert.Empty(result);
    }

    [Fact]
    public void Itemize_only_empty_runs_returns_empty()
    {
        var runs = new List<TextRun>
        {
            new("", MakeStyle()),
            new("", MakeStyle()),
        };
        var result = LineBuilder.Itemize(runs, ParagraphDirection.LeftToRight);
        Assert.Empty(result);
    }

    // --- Single LTR run -------------------------------------------

    [Fact]
    public void Itemize_single_LTR_run_returns_one_LTR_itemized_run()
    {
        var runs = new List<TextRun>
        {
            new("Hello", MakeStyle()),
        };
        var result = LineBuilder.Itemize(runs, ParagraphDirection.LeftToRight);
        Assert.Single(result);
        Assert.Equal(0, result[0].Utf16Start);
        Assert.Equal(5, result[0].Utf16Length);
        Assert.Equal(0, result[0].BidiLevel);
        Assert.False(result[0].IsRtl);
        Assert.Equal(0, result[0].SourceTextRunIndex);
    }

    // --- Single RTL run -------------------------------------------

    [Fact]
    public void Itemize_single_RTL_run_returns_one_RTL_itemized_run()
    {
        // Arabic "Marhaban" = "مرحبا" (5 code units).
        var runs = new List<TextRun>
        {
            new("مرحبا", MakeStyle()),
        };
        var result = LineBuilder.Itemize(runs, ParagraphDirection.RightToLeft);
        Assert.Single(result);
        Assert.Equal(5, result[0].Utf16Length);
        Assert.True(result[0].IsRtl);
        Assert.Equal(1, result[0].BidiLevel);
    }

    // --- Mixed LTR + RTL ------------------------------------------

    [Fact]
    public void Itemize_mixed_LTR_RTL_in_LTR_paragraph_creates_level_boundary()
    {
        // LTR paragraph "Hello مرحبا World":
        //   "Hello " — level 0 (LTR)
        //   "مرحبا" — level 1 (RTL embedding)
        //   " World" — level 0 (LTR)
        // We expect at least 3 runs at level boundaries (the
        // "Hello " + leading space gets its own level boundary
        // depending on bidi class).
        var runs = new List<TextRun>
        {
            new("Hello مرحبا World", MakeStyle()),
        };
        var result = LineBuilder.Itemize(runs, ParagraphDirection.LeftToRight);
        // At least one RTL run + one LTR run.
        var hasLtr = false;
        var hasRtl = false;
        foreach (var r in result)
        {
            if (r.IsRtl) hasRtl = true;
            else hasLtr = true;
        }
        Assert.True(hasLtr, "Expected at least one LTR itemized run.");
        Assert.True(hasRtl, "Expected at least one RTL itemized run.");
        // Total length covers entire input.
        var totalCovered = 0;
        foreach (var r in result) totalCovered += r.Utf16Length;
        Assert.Equal(runs[0].Text.Length, totalCovered);
    }

    // --- Style boundary ------------------------------------------

    [Fact]
    public void Itemize_two_runs_same_direction_different_style_creates_run_boundary()
    {
        // Per cycle 1 post-PR-32 review (Copilot #2) — boundary is
        // created on SourceTextRunIndex change regardless of style
        // equality. Even when the runs have identical styles, they
        // remain SEPARATE source-tree boxes (each one might be a
        // distinct InlineBox / TextRun in the box tree, even if both
        // happen to compute the same style). The cycle-1 itemizer
        // correctly emits one ItemizedRun per source TextRun.
        //
        // To make the "different style" claim observable too, set
        // distinguishing properties on the second style: different
        // font-size makes the styles non-equal AND would affect the
        // shaper output downstream (cycle 2).
        var style1 = MakeStyle();
        var style2 = MakeStyle();
        SetLengthPx(style2, NetPdf.Css.Properties.PropertyId.FontSize, 24);
        var runs = new List<TextRun>
        {
            new("foo", style1),
            new("bar", style2),
        };
        var result = LineBuilder.Itemize(runs, ParagraphDirection.LeftToRight);
        Assert.Equal(2, result.Length);
        Assert.Equal(0, result[0].SourceTextRunIndex);
        Assert.Equal(1, result[1].SourceTextRunIndex);
        Assert.Equal(0, result[0].Utf16Start);
        Assert.Equal(3, result[0].Utf16Length);
        Assert.Equal(3, result[1].Utf16Start);
        Assert.Equal(3, result[1].Utf16Length);
    }

    [Fact]
    public void Itemize_two_runs_same_direction_identical_style_still_creates_source_run_boundary()
    {
        // Mirror of the test above — even when styles are identical,
        // a source-run change creates an ItemizedRun boundary. Pinning
        // this so the cycle-1 source-run-boundary contract is explicit.
        var runs = new List<TextRun>
        {
            new("foo", MakeStyle()),
            new("bar", MakeStyle()),
        };
        var result = LineBuilder.Itemize(runs, ParagraphDirection.LeftToRight);
        Assert.Equal(2, result.Length);
        Assert.Equal(0, result[0].SourceTextRunIndex);
        Assert.Equal(1, result[1].SourceTextRunIndex);
    }

    // --- Mandatory line break preserved ---------------------------

    [Fact]
    public void Itemize_text_with_LF_keeps_LF_in_run()
    {
        // The cycle 1 itemizer doesn't split on line-break opportunities;
        // it preserves the LF for cycle 3's wrapper to see as
        // LineBreakOpportunity.Mandatory.
        var runs = new List<TextRun>
        {
            new("line1\nline2", MakeStyle()),
        };
        var result = LineBuilder.Itemize(runs, ParagraphDirection.LeftToRight);
        // Single LTR run covering the whole text (LF doesn't change
        // bidi level).
        Assert.Single(result);
        Assert.Equal(11, result[0].Utf16Length);
    }

    // --- Coverage invariant ---------------------------------------

    [Fact]
    public void Itemize_emitted_runs_cover_full_text_in_order_with_no_gaps_or_overlaps()
    {
        var runs = new List<TextRun>
        {
            new("aaa", MakeStyle()),
            new("bbb", MakeStyle()),
            new("ccc", MakeStyle()),
        };
        var result = LineBuilder.Itemize(runs, ParagraphDirection.LeftToRight);
        var expectedNext = 0;
        foreach (var r in result)
        {
            Assert.Equal(expectedNext, r.Utf16Start);
            expectedNext = r.Utf16Start + r.Utf16Length;
        }
        Assert.Equal(9, expectedNext);
    }

    // --- Auto direction -------------------------------------------

    [Fact]
    public void Itemize_Auto_direction_LTR_first_strong_resolves_to_level_0()
    {
        var runs = new List<TextRun>
        {
            new("Hello", MakeStyle()),
        };
        var result = LineBuilder.Itemize(runs, ParagraphDirection.Auto);
        Assert.Single(result);
        Assert.Equal(0, result[0].BidiLevel);
    }

    [Fact]
    public void Itemize_Auto_direction_RTL_first_strong_resolves_to_level_1()
    {
        var runs = new List<TextRun>
        {
            new("مرحبا", MakeStyle()),  // Marhaban
        };
        var result = LineBuilder.Itemize(runs, ParagraphDirection.Auto);
        Assert.Single(result);
        Assert.Equal(1, result[0].BidiLevel);
        Assert.True(result[0].IsRtl);
    }

    // --- Surrogate pair ------------------------------------------

    [Fact]
    public void Itemize_surrogate_pair_kept_as_single_unit()
    {
        // U+1F600 (😀) = surrogate pair (D83D DE00) — 2 UTF-16 code
        // units. Bidi treats both code units identically (same
        // codepoint); the itemized run covers both.
        var runs = new List<TextRun>
        {
            new("hi😀bye", MakeStyle()),
        };
        var result = LineBuilder.Itemize(runs, ParagraphDirection.LeftToRight);
        // Single LTR run covering all 7 code units.
        Assert.Single(result);
        Assert.Equal(7, result[0].Utf16Length);
    }

    // --- ItemizedRun.IsRtl ----------------------------------------

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void ItemizedRun_IsRtl_returns_true_for_odd_levels(byte level, bool expected)
    {
        var run = new ItemizedRun(0, 1, level, 0);
        Assert.Equal(expected, run.IsRtl);
    }

    // --- ArgumentNullException -----------------------------------

    [Fact]
    public void Itemize_null_runs_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LineBuilder.Itemize(null!, ParagraphDirection.LeftToRight));
    }

    // ====================================================================
    //  Phase 3 Task 9 cycle 1 post-PR-32 review tests
    // ====================================================================

    [Fact]
    public void PostPr32_multi_paragraph_LTR_RTL_each_resolves_independently_under_Auto()
    {
        // Per cycle 1 post-PR-32 review (Copilot #1) — pre-fix used
        // ParagraphLevelResolver.Resolve on the FULL concatenated text,
        // which scans only until the first B-class character. Under
        // Auto direction, a "EnglishText\nالعربية" input would resolve
        // BOTH paragraphs to LTR (paragraph 1's first-strong char wins
        // the scan even for paragraph 2). Post-fix uses
        // BidiAlgorithm.ResolveLevels which segments per UAX #9 §3.3.1
        // P1 + resolves each paragraph independently.
        //
        // Test: "Hello\nمرحبا" with ParagraphDirection.Auto.
        // Pre-fix: levels [0,0,0,0,0,0,0,0,0,0,0] (RTL paragraph
        //   incorrectly resolved as LTR because the FIRST paragraph's
        //   first-strong was 'H' = LTR).
        // Post-fix: paragraph 1 ("Hello\n") at level 0; paragraph 2
        //   ("مرحبا") at level 1.
        var runs = new List<TextRun>
        {
            new("Hello\nمرحبا", MakeStyle()),
        };
        var result = LineBuilder.Itemize(runs, ParagraphDirection.Auto);
        // Multi-paragraph input must produce at least an LTR run for
        // paragraph 1 + an RTL run for paragraph 2.
        var hasLtr = false;
        var hasRtl = false;
        foreach (var r in result)
        {
            if (r.IsRtl) hasRtl = true;
            else hasLtr = true;
        }
        Assert.True(hasLtr, "Expected an LTR run for the English paragraph.");
        Assert.True(hasRtl,
            "Expected an RTL run for the Arabic paragraph (post-Copilot-#1 fix). "
            + "Pre-fix would have resolved both paragraphs as LTR because "
            + "ParagraphLevelResolver.Resolve's Auto scan stopped at the first B "
            + "character (the LF), inheriting paragraph 1's level.");
    }

    [Fact]
    public void PostPr32_multi_paragraph_with_explicit_LTR_does_not_drop_subsequent_paragraphs()
    {
        // Sanity: explicit LTR direction should still produce coverage
        // across the full multi-paragraph input. Pre-fix coverage was
        // also broken on multi-paragraph; post-fix uses
        // BidiAlgorithm.ResolveLevels which correctly handles the
        // full input.
        var runs = new List<TextRun>
        {
            new("first paragraph\nsecond paragraph\nthird", MakeStyle()),
        };
        var result = LineBuilder.Itemize(runs, ParagraphDirection.LeftToRight);
        // Coverage invariant — full input covered.
        var totalCovered = 0;
        foreach (var r in result) totalCovered += r.Utf16Length;
        Assert.Equal(runs[0].Text.Length, totalCovered);
    }

    [Fact]
    public void PostPr32_multi_paragraph_RTL_then_LTR_produces_two_distinct_levels()
    {
        // Auto direction: paragraph 1 = "مرحبا\n" (RTL), paragraph 2 =
        // "Hello" (LTR). Each paragraph's first-strong drives its OWN
        // level after the fix.
        var runs = new List<TextRun>
        {
            new("مرحبا\nHello", MakeStyle()),
        };
        var result = LineBuilder.Itemize(runs, ParagraphDirection.Auto);
        var hasLtr = false;
        var hasRtl = false;
        foreach (var r in result)
        {
            if (r.IsRtl) hasRtl = true;
            else hasLtr = true;
        }
        Assert.True(hasLtr, "Expected an LTR run for the second-paragraph English text.");
        Assert.True(hasRtl, "Expected an RTL run for the first-paragraph Arabic text.");
    }

    // --- UAX #24 script-change itemization (uax-24-script-detection) ---

    [Fact]
    public void Itemize_splits_same_direction_runs_on_a_script_change()
    {
        // "abc中" — Latin + Han are BOTH left-to-right (one bidi level), so only the UAX #24 script
        // change splits them: Latin "abc" (Latn) then Han "中" (Hani). Proves script-change itemization
        // (not just bidi) opens the boundary, so each shapes with its own OpenType feature set.
        var runs = new List<TextRun> { new("abc中", MakeStyle()) };
        var itemized = LineBuilder.Itemize(runs, ParagraphDirection.LeftToRight);

        Assert.Equal(2, itemized.Length);
        Assert.Equal("Latn", itemized[0].ScriptIso15924);
        Assert.Equal(0, itemized[0].Utf16Start);
        Assert.Equal(3, itemized[0].Utf16Length);
        Assert.Equal("Hani", itemized[1].ScriptIso15924);
        Assert.Equal(3, itemized[1].Utf16Start);
        Assert.Equal(1, itemized[1].Utf16Length);
    }

    [Fact]
    public void Itemize_keeps_a_latin_run_with_common_codepoints_together()
    {
        // Common codepoints (comma, space, digits) EXTEND the surrounding script rather than splitting
        // the run, so "Hello, World 123" is a single Latn run.
        var runs = new List<TextRun> { new("Hello, World 123", MakeStyle()) };
        var itemized = LineBuilder.Itemize(runs, ParagraphDirection.LeftToRight);

        Assert.Single(itemized);
        Assert.Equal("Latn", itemized[0].ScriptIso15924);
    }

    [Fact]
    public void Itemize_leading_common_prefix_adopts_the_following_script()
    {
        // A leading Common prefix takes the script that follows it (UAX #24 §5.1): "123中文" is ONE Han
        // run (Hani), not a Common-then-Han split.
        var runs = new List<TextRun> { new("123中文", MakeStyle()) };
        var itemized = LineBuilder.Itemize(runs, ParagraphDirection.LeftToRight);

        Assert.Single(itemized);
        Assert.Equal("Hani", itemized[0].ScriptIso15924);
    }

    [Fact]
    public void Itemize_all_common_run_carries_no_script()
    {
        // Pure digits / punctuation carry no script of their own (null), so the shaper falls back to the
        // caller's uniform script.
        var runs = new List<TextRun> { new("12.34", MakeStyle()) };
        var itemized = LineBuilder.Itemize(runs, ParagraphDirection.LeftToRight);

        Assert.Single(itemized);
        Assert.Null(itemized[0].ScriptIso15924);
    }

    // --- Helpers -------------------------------------------------

    private static ComputedStyle MakeStyle() =>
        ComputedStyle.RentForExclusiveTesting();

    private static void SetLengthPx(ComputedStyle style, NetPdf.Css.Properties.PropertyId id, double px) =>
        style.Set(id, NetPdf.Css.ComputedValues.ComputedSlot.FromLengthPx(px));
}
