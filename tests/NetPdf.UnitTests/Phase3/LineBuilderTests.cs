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
        // Two LTR text runs with different styles → 2 itemized runs
        // (boundary at the source-run change even though bidi level
        // is identical).
        var runs = new List<TextRun>
        {
            new("foo", MakeStyle()),
            new("bar", MakeStyle()),
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

    // --- Helpers -------------------------------------------------

    private static ComputedStyle MakeStyle() =>
        ComputedStyle.RentForExclusiveTesting();
}
