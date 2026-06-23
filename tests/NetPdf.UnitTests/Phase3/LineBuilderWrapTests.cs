// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
using NetPdf.Css.ComputedValues;
using NetPdf.Layout.Inline;
using NetPdf.Text.Bidi;
using NetPdf.Text.Shaping;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>Per Phase 3 Task 9 cycle 3a — tests for
/// <see cref="LineBuilder.Wrap"/>. Cycle 3a is the naive greedy
/// wrapper: walk shaped glyphs, snap back to the most recent
/// UAX #14 <c>Allowed</c> opportunity on overflow, force fragment
/// boundaries on <c>Mandatory</c> opportunities, fall through to
/// overflow when no candidate exists. Cycle 3b/c add white-space
/// variants, hyphenation, overflow-wrap, word-break, text-align,
/// vertical-align, RTL fragment-level reversal.</summary>
public class LineBuilderWrapTests
{
    private const string LatnScript = "Latn";
    private const string EnLang = "en";
    private const float SyntheticAdvanceA = 12f * 500f / 1000f; // glyph 1 has 500 fontUnit advance, fontSize=12, unitsPerEm=1000 → 6px

    // --- Empty / null inputs -------------------------------------

    [Fact]
    public void Wrap_empty_shapedRuns_returns_empty()
    {
        var result = LineBuilder.Wrap(
            sourceTextRuns: Array.Empty<TextRun>(),
            shapedRuns: Array.Empty<ShapedRun>(),
            availableInlineSize: 100);
        Assert.Empty(result);
    }

    [Fact]
    public void Wrap_null_sourceTextRuns_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LineBuilder.Wrap(null!, Array.Empty<ShapedRun>(), 100));
    }

    [Fact]
    public void Wrap_null_shapedRuns_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LineBuilder.Wrap(Array.Empty<TextRun>(), null!, 100));
    }

    [Fact]
    public void Wrap_zero_inline_size_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LineBuilder.Wrap(Array.Empty<TextRun>(), Array.Empty<ShapedRun>(), 0));
    }

    [Fact]
    public void Wrap_negative_inline_size_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LineBuilder.Wrap(Array.Empty<TextRun>(), Array.Empty<ShapedRun>(), -10));
    }

    [Fact]
    public void Wrap_nan_inline_size_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LineBuilder.Wrap(Array.Empty<TextRun>(), Array.Empty<ShapedRun>(), double.NaN));
    }

    [Fact]
    public void Wrap_infinity_inline_size_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LineBuilder.Wrap(Array.Empty<TextRun>(), Array.Empty<ShapedRun>(),
                double.PositiveInfinity));
    }

    // --- Single line that fits -----------------------------------

    [Fact]
    public void Wrap_single_short_run_fits_in_one_line()
    {
        // "AAA" is 3 glyphs × 6px = 18px; available = 100px; fits.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 100);

        Assert.Single(lines);
        Assert.Single(lines[0].Slices);
        var slice = lines[0].Slices[0];
        Assert.Equal(0, slice.ShapedRunIndex);
        Assert.Equal(0, slice.GlyphStart);
        Assert.Equal(3, slice.GlyphLength);
        Assert.True(lines[0].TotalAdvance > 0);
    }

    // --- Soft wrap on Allowed break ------------------------------

    [Fact]
    public void Wrap_soft_wrap_on_space_separator()
    {
        // "AAA AAA" — UAX #14 Allowed break after the SP. Width per A
        // ≈ 6px; "AAA " ≈ 24px (SP advance is 0 in this synthetic
        // font but the break point is still recognized). Setting
        // available = 25px should force a wrap after "AAA ".
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA AAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 25);

        Assert.Equal(2, lines.Length);
        // First line should NOT end with mandatory break (soft wrap).
        Assert.False(lines[0].EndsWithMandatoryBreak);
    }

    [Fact]
    public void Wrap_break_spaces_adds_a_break_after_every_space()
    {
        // white-space: break-spaces (CSS Text L3 §6.4) — a soft-wrap opportunity exists AFTER every
        // preserved space, INCLUDING between consecutive spaces, vs pre-wrap which only breaks at the
        // UAX #14 Allowed positions (after the space sequence). With a run of preserved spaces in a
        // line too narrow to hold them, break-spaces wraps across MORE lines than pre-wrap.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("A          A", MakeStyle()) };   // A + 10 spaces + A
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);

        var preWrap = LineBuilder.Wrap(
            sourceRuns, shaped, availableInlineSize: 18, whiteSpace: WhiteSpace.PreWrap);
        var breakSpaces = LineBuilder.Wrap(
            sourceRuns, shaped, availableInlineSize: 18, whiteSpace: WhiteSpace.BreakSpaces);

        Assert.True(breakSpaces.Length > preWrap.Length,
            $"break-spaces ({breakSpaces.Length} lines) should break the space run more than " +
            $"pre-wrap ({preWrap.Length} lines)");
    }

    [Fact]
    public void Wrap_break_spaces_keeps_trailing_space_advance_at_line_end()
    {
        // white-space: break-spaces (CSS Text L3 §6.4, PR #212 review P3) — trailing preserved spaces at
        // a line end TAKE their advance (they do not hang or get trimmed), unlike collapse modes which
        // strip end-of-line spaces. "AAA   AAA" wrapped so the first line ends in preserved spaces: under
        // break-spaces the first line's advance INCLUDES those spaces; under normal (collapse) the
        // end-of-line spaces are trimmed, so its first line is narrower.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA   AAA", MakeStyle()) };   // 3 interior spaces
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);

        var breakSpaces = LineBuilder.Wrap(
            sourceRuns, shaped, availableInlineSize: 30, whiteSpace: WhiteSpace.BreakSpaces);
        var normal = LineBuilder.Wrap(
            sourceRuns, shaped, availableInlineSize: 30, whiteSpace: WhiteSpace.Normal);

        Assert.True(breakSpaces[0].TotalAdvance > normal[0].TotalAdvance,
            $"break-spaces line 0 ({breakSpaces[0].TotalAdvance}) should carry the trailing-space advance " +
            $"that normal trims ({normal[0].TotalAdvance})");
    }

    [Fact]
    public void Wrap_break_spaces_is_honored_per_source_run_not_globally()
    {
        // PR #212 review P3 — break-spaces must be honored PER SOURCE RUN (via inlineTextPolicyPerRun),
        // not as a global mode. Two runs; only run 1 carries break-spaces. With run 1 = break-spaces its
        // spaces add per-space break opportunities (more wrapping) vs run 1 = pre-wrap (UAX #14 only) —
        // proving the per-run policy drives the behavior, not a single global white-space.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("A", MakeStyle()),               // run 0 — normal
            new("A          A", MakeStyle()),     // run 1 — A + 10 CONSECUTIVE spaces + A
        };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);

        InlineTextPolicy[] PerRun(WhiteSpace run1Ws) => new[]
        {
            new InlineTextPolicy(WhiteSpace.Normal, OverflowWrap.Normal, WordBreak.Normal, Hyphens.Manual),
            new InlineTextPolicy(run1Ws, OverflowWrap.Normal, WordBreak.Normal, Hyphens.Manual),
        };

        var run1BreakSpaces = LineBuilder.Wrap(
            sourceRuns, shaped, availableInlineSize: 18, inlineTextPolicyPerRun: PerRun(WhiteSpace.BreakSpaces));
        var run1PreWrap = LineBuilder.Wrap(
            sourceRuns, shaped, availableInlineSize: 18, inlineTextPolicyPerRun: PerRun(WhiteSpace.PreWrap));

        Assert.True(run1BreakSpaces.Length > run1PreWrap.Length,
            $"break-spaces on ONLY run 1 ({run1BreakSpaces.Length} lines) should break its spaces more " +
            $"than pre-wrap on run 1 ({run1PreWrap.Length} lines)");
    }

    // --- Mandatory break (LF) ------------------------------------

    [Fact]
    public void Wrap_mandatory_break_at_LF_terminates_line()
    {
        // "AA\nAA" — UAX #14 Mandatory after the LF (LB5). Available
        // = 1000px so no soft-wrap; only the mandatory break splits
        // the input. (Synthetic font has no 'B' glyph, so we use 'A'
        // on both sides of the LF.)
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA\nAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000);

        Assert.Equal(2, lines.Length);
        Assert.True(lines[0].EndsWithMandatoryBreak,
            "First line should end with mandatory break (after LF).");
    }

    // --- Overflow without break candidate ------------------------

    [Fact]
    public void Wrap_long_unbreakable_run_overflows_without_snap_back()
    {
        // "AAAAAAAAA" (no spaces, no break opportunities except end).
        // Width = 9 × 6 = 54px; available = 10px. Cycle 3a allows
        // overflow when no candidate break exists in the line.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAAAAAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 10);

        // Cycle 3a: no break candidate → all glyphs end up on one
        // overflowing line (cycle 3b will add overflow-wrap).
        Assert.Single(lines);
        var totalGlyphs = 0;
        foreach (var slice in lines[0].Slices) totalGlyphs += slice.GlyphLength;
        Assert.Equal(9, totalGlyphs);
        Assert.True(lines[0].TotalAdvance > 10,
            "Cycle 3a allows the line to overflow when no break candidate exists.");
    }

    // --- Multi shaped-run --------------------------------------

    [Fact]
    public void Wrap_line_spanning_multiple_shaped_runs_emits_one_slice_per_run()
    {
        // Two source TextRuns "AA" + "AA" with distinct styles →
        // two ItemizedRuns → two ShapedRuns. Available = 100px (fits
        // everything on one line). Output: one LineFragment with two
        // Slices. (Synthetic font supports only 'A' / 'B'; we use
        // distinct styles on identical text to exercise the multi-
        // run slice path without relying on glyph variety.)
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AA", MakeStyle()),
            new("AA", MakeStyle()),
        };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 100);

        Assert.Single(lines);
        Assert.Equal(2, lines[0].Slices.Length);
        Assert.Equal(0, lines[0].Slices[0].ShapedRunIndex);
        Assert.Equal(1, lines[0].Slices[1].ShapedRunIndex);
        Assert.Equal(2, lines[0].Slices[0].GlyphLength);
        Assert.Equal(2, lines[0].Slices[1].GlyphLength);
    }

    // --- Total advance invariant ---------------------------------

    [Fact]
    public void Wrap_total_advance_equals_sum_of_slice_advances()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AA", MakeStyle()),
            new("AA", MakeStyle()),
        };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 100);

        var line = lines[0];
        var summed = 0.0;
        foreach (var s in line.Slices) summed += s.SliceAdvance;
        Assert.Equal(summed, line.TotalAdvance, precision: 4);
    }

    // --- CancellationToken ---------------------------------------

    [Fact]
    public void Wrap_observes_cancelled_token()
    {
        // Many lines via mandatory breaks; cancel before any line
        // emission. The token is checked at method entry + on every
        // expensive loop boundary + before tail emit, so a pre-
        // cancelled token throws even on a single-line unbreakable
        // input that never enters the wrap loop's emit branches.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("A\nA\nA\nA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 100, cancellationToken: cts.Token));
    }

    // --- Cycle 3a hardening — mandatory-break control glyph trim --

    [Fact]
    public void Wrap_LF_glyph_excluded_from_drawable_slice()
    {
        // "AA\nAA" — line 1 should contain ONLY the two leading 'A'
        // glyphs (LF trimmed off), even though the LF glyph
        // contributed to the wrap-loop's break detection.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA\nAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000);

        Assert.Equal(2, lines.Length);
        // Line 1: drawable count = 2 (the AA before the LF).
        var line1Glyphs = 0;
        foreach (var s in lines[0].Slices) line1Glyphs += s.GlyphLength;
        Assert.Equal(2, line1Glyphs);
        Assert.True(lines[0].EndsWithMandatoryBreak);
    }

    [Fact]
    public void Wrap_CRLF_strips_both_CR_and_LF_from_drawable_slice()
    {
        // "AA\r\nAA" — line 1 should contain ONLY the two leading
        // 'A' glyphs; both CR and LF are mandatory-control glyphs
        // and trim off the drawable slice. UAX #14 LB5 treats CR×LF
        // as a single break, so only one fragment boundary fires.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA\r\nAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000);

        Assert.Equal(2, lines.Length);
        var line1Glyphs = 0;
        foreach (var s in lines[0].Slices) line1Glyphs += s.GlyphLength;
        Assert.Equal(2, line1Glyphs);
        Assert.True(lines[0].EndsWithMandatoryBreak);
    }

    [Fact]
    public void Wrap_trailing_LF_emits_one_drawable_line_then_empty_terminator()
    {
        // "AA\n" — drawable line "AA" terminated by mandatory; tail
        // after the LF has no glyphs so no second fragment.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA\n", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000);

        Assert.Single(lines);
        var glyphCount = 0;
        foreach (var s in lines[0].Slices) glyphCount += s.GlyphLength;
        Assert.Equal(2, glyphCount);
        Assert.True(lines[0].EndsWithMandatoryBreak);
    }

    [Fact]
    public void Wrap_lone_LF_emits_empty_drawable_fragment()
    {
        // "\n" — a single mandatory-control glyph, no drawable text.
        // Cycle 3a emits a LineFragment with empty Slices +
        // EndsWithMandatoryBreak=true so the painter knows to advance
        // the baseline without drawing anything.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("\n", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000);

        Assert.Single(lines);
        Assert.Empty(lines[0].Slices);
        Assert.True(lines[0].EndsWithMandatoryBreak);
        Assert.Equal(0, lines[0].TotalAdvance);
    }

    // --- Cycle 3a hardening — cluster-end break-opportunity --------

    [Fact]
    public void Wrap_surrogate_pair_glyph_does_not_crash_break_lookup()
    {
        // "A😀A" — the emoji is a surrogate pair (U+1F600 = 0xD83D
        // 0xDE00). Synthetic font has no glyph for the emoji; the
        // emoji shapes as .notdef. Cluster-END mapping must use the
        // surrogate pair's END (cluster + 2), not the START. Test
        // ensures no out-of-range break-opp lookup + the wrap
        // completes without throwing.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("A😀A", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        // Cycle 3a regression: should not throw IndexOutOfRange or
        // ArgumentOutOfRange — the cluster-end fix maps each glyph
        // to a valid breaks[] index regardless of multi-codeunit
        // cluster span.
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000);
        Assert.NotEmpty(lines);
    }

    [Fact]
    public void Wrap_collapsed_spaces_pinned_for_cycle_3b_white_space_normal()
    {
        // Cycle 3a does NOT preprocess CSS white-space — input is
        // wrapped AS-IS. "AA  AA" (TWO spaces) keeps both spaces;
        // cycle 3b's white-space:normal will collapse them. This
        // test pins the cycle-3a behavior so cycle-3b's preprocessor
        // change is detected.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA  AA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000);

        // All glyphs (including both spaces) flow into one line —
        // 6 glyphs total (4 'A' + 2 ' ').
        Assert.Single(lines);
        var totalGlyphs = 0;
        foreach (var s in lines[0].Slices) totalGlyphs += s.GlyphLength;
        Assert.Equal(6, totalGlyphs);
    }

    [Fact]
    public void Wrap_combining_mark_does_not_split_cluster_advance()
    {
        // "Á" — Latin A + COMBINING ACUTE ACCENT (single
        // grapheme, possibly multi-codepoint cluster depending on
        // font shaping). Synthetic font has no acute combining mark;
        // it shapes as .notdef. The cluster-end mapping must keep
        // the combining mark's advance attached to the base.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("ÁA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        // Should complete without throwing — cluster-end lookup
        // doesn't go out of range when the combining mark's cluster
        // overlaps the base 'A'.
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000);
        Assert.NotEmpty(lines);
    }

    // --- Cycle 3a hardening — pre-cancelled fast-path -------------

    [Fact]
    public void Wrap_precancelled_token_throws_at_method_entry_for_unbreakable_input()
    {
        // Pre-cancel before Wrap. Input is a single unbreakable run
        // that fits in one line — without entry-level cancellation,
        // the wrap loop wouldn't fire any cancellation check (no
        // soft-wrap, no mandatory). The entry-level
        // ThrowIfCancellationRequested fires immediately.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000, cancellationToken: cts.Token));
    }

    // --- Cycle 3a hardening — coherence validation ----------------

    [Fact]
    public void Wrap_throws_when_shapedRun_SourceTextRunIndex_out_of_range()
    {
        // Hand-built mismatched ShapedRun — claims SourceTextRunIndex=5
        // but sourceTextRuns has 1 entry. Should throw
        // ArgumentException with descriptive message.
        var sourceRuns = new List<TextRun> { new("AA", MakeStyle()) };
        var bogusItemized = new ItemizedRun(0, 2, 0, SourceTextRunIndex: 5);
        var bogusGlyph = new ShapedGlyph(GlyphId: 1, XAdvance: 6,
            YAdvance: 0, XOffset: 0, YOffset: 0, Cluster: 0);
        var shaped = new[] { new ShapedRun(bogusItemized, new[] { bogusGlyph }, 6.0) };
        var ex = Assert.Throws<ArgumentException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000));
        Assert.Contains("SourceTextRunIndex", ex.Message);
    }

    [Fact]
    public void Wrap_throws_when_shapedRun_Source_range_overflows_concat()
    {
        var sourceRuns = new List<TextRun> { new("AA", MakeStyle()) };
        // Source range [0, 100) overflows concat length 2.
        var bogusItemized = new ItemizedRun(0, 100, 0, 0);
        var glyph = new ShapedGlyph(GlyphId: 1, XAdvance: 6,
            YAdvance: 0, XOffset: 0, YOffset: 0, Cluster: 0);
        var shaped = new[] { new ShapedRun(bogusItemized, new[] { glyph }, 6.0) };
        var ex = Assert.Throws<ArgumentException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000));
        Assert.Contains("Utf16", ex.Message);
    }

    [Fact]
    public void Wrap_throws_when_glyph_Cluster_out_of_range()
    {
        var sourceRuns = new List<TextRun> { new("AA", MakeStyle()) };
        var itemized = new ItemizedRun(0, 2, 0, 0);
        // Cluster=99 is past concat length (2).
        var bogusGlyph = new ShapedGlyph(GlyphId: 1, XAdvance: 6,
            YAdvance: 0, XOffset: 0, YOffset: 0, Cluster: 99);
        var shaped = new[] { new ShapedRun(itemized, new[] { bogusGlyph }, 6.0) };
        var ex = Assert.Throws<ArgumentException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000));
        Assert.Contains("Cluster", ex.Message);
    }

    [Fact]
    public void Wrap_throws_when_glyph_XAdvance_is_NaN()
    {
        var sourceRuns = new List<TextRun> { new("AA", MakeStyle()) };
        var itemized = new ItemizedRun(0, 2, 0, 0);
        var bogusGlyph = new ShapedGlyph(GlyphId: 1, XAdvance: float.NaN,
            YAdvance: 0, XOffset: 0, YOffset: 0, Cluster: 0);
        var shaped = new[] { new ShapedRun(itemized, new[] { bogusGlyph }, 0) };
        var ex = Assert.Throws<ArgumentException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000));
        Assert.Contains("XAdvance", ex.Message);
    }

    [Fact]
    public void Wrap_throws_when_glyph_XAdvance_is_negative()
    {
        var sourceRuns = new List<TextRun> { new("AA", MakeStyle()) };
        var itemized = new ItemizedRun(0, 2, 0, 0);
        var bogusGlyph = new ShapedGlyph(GlyphId: 1, XAdvance: -1f,
            YAdvance: 0, XOffset: 0, YOffset: 0, Cluster: 0);
        var shaped = new[] { new ShapedRun(itemized, new[] { bogusGlyph }, -1) };
        var ex = Assert.Throws<ArgumentException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000));
        Assert.Contains("XAdvance", ex.Message);
    }

    // --- UAX #9 L2 level-aware slice reordering (rtl-fragment-reversal) ---
    //
    // The per-run bidi LEVEL (resolved by Itemize, including the Auto first-strong rule) drives L2
    // reordering of a line's slices. These tests cover the three structural cases + the byte-identity
    // guard. The SyntheticFont maps every char to .notdef but the LEVELS come from the real codepoints,
    // so Hebrew (א/ב) gives RTL (level 1) runs and Latin gives LTR runs.

    private static double LeftXOfRun(LineFragment line, int shapedRunIndex)
    {
        var x = 0.0;
        foreach (var s in line.Slices)
        {
            if (s.ShapedRunIndex == shapedRunIndex) return x;
            x += s.SliceAdvance;
        }
        return double.NaN;
    }

    [Fact]
    public void Wrap_reverses_rtl_runs_in_an_rtl_paragraph()
    {
        // Two Hebrew runs (level 1) under an RTL base → L2 reverses the whole line: the first logical run
        // paints rightmost, the last leftmost. TotalAdvance (order-independent) is unchanged.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("א", MakeStyle()), new("ב", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.RightToLeft);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, "Hebr", "he");

        var line = Assert.Single(LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000));

        Assert.Equal(2, line.Slices.Length);
        Assert.Equal(1, line.Slices[0].ShapedRunIndex);  // run1 (last logical) leftmost
        Assert.Equal(0, line.Slices[1].ShapedRunIndex);  // run0 (first logical) rightmost
        Assert.Equal(0.0, LeftXOfRun(line, 1));
        Assert.True(LeftXOfRun(line, 0) > LeftXOfRun(line, 1));
    }

    [Fact]
    public void Wrap_keeps_latin_runs_in_document_order_in_an_rtl_paragraph()
    {
        // Two LATIN runs in an RTL paragraph are a single LTR embedding (level 2), which reads
        // left-to-right — so L2 double-reverses them (reverse at level 2, then at level 1) back to
        // DOCUMENT order. (A naive "flat reverse for RTL base" got this wrong — PR #214 review [P1].)
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AB", MakeStyle()), new("CD", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.RightToLeft);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);

        var line = Assert.Single(LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000));

        Assert.Equal(2, line.Slices.Length);
        Assert.Equal(0, line.Slices[0].ShapedRunIndex);  // run0 stays leftmost (Latin reads L→R)
        Assert.Equal(1, line.Slices[1].ShapedRunIndex);
    }

    [Fact]
    public void Wrap_reverses_only_the_embedded_rtl_span_in_an_ltr_paragraph()
    {
        // PR #214 review [P1] — the common "English with a Hebrew name" case: Latin + two Hebrew runs +
        // Latin under an LTR base. L2 reverses ONLY the contiguous RTL (level-1) span; the surrounding
        // Latin (level 0) keeps document order. Logical [A, א, ב, B] → visual [A, ב, א, B].
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("A", MakeStyle()), new("א", MakeStyle()), new("ב", MakeStyle()), new("B", MakeStyle()),
        };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);

        var line = Assert.Single(LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000));

        Assert.Equal(4, line.Slices.Length);
        Assert.Equal(0, line.Slices[0].ShapedRunIndex);  // A  (LTR, stays)
        Assert.Equal(2, line.Slices[1].ShapedRunIndex);  // ב  (RTL span reversed)
        Assert.Equal(1, line.Slices[2].ShapedRunIndex);  // א
        Assert.Equal(3, line.Slices[3].ShapedRunIndex);  // B  (LTR, stays)
    }

    [Fact]
    public void Wrap_keeps_document_order_for_an_all_ltr_line()
    {
        // Byte-identity guard: a pure-LTR line (no odd level) is left untouched by L2.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AB", MakeStyle()), new("CD", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);

        var line = Assert.Single(LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000));

        Assert.Equal(0, line.Slices[0].ShapedRunIndex);
        Assert.Equal(1, line.Slices[1].ShapedRunIndex);
    }

    [Fact]
    public void Wrap_reverses_rtl_runs_for_an_auto_base_that_resolves_to_rtl()
    {
        // `ParagraphDirection.Auto` resolves the base via the UAX #9 P2/P3 first-strong rule at itemize
        // time, so two Hebrew runs under Auto get level 1 and L2 reverses them — handled entirely by the
        // run levels (no separate base-direction pass in Wrap).
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("א", MakeStyle()), new("ב", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.Auto);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, "Hebr", "he");

        var line = Assert.Single(LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000));

        Assert.Equal(2, line.Slices.Length);
        Assert.Equal(1, line.Slices[0].ShapedRunIndex);
        Assert.Equal(0, line.Slices[1].ShapedRunIndex);
    }

    // --- word-break: keep-all (CSS Text §5.2 / UAX #14 LB30b, word-break-keep-all-cjk) ---

    [Fact]
    public void Wrap_keep_all_suppresses_inter_cjk_breaks()
    {
        // Three Han ideographs (each falls back to .notdef at 600 fontUnits = 7.2px). At width 10,
        // NORMAL word-break wraps per character — UAX #14 leaves a default break opportunity between
        // two ID-class ideographs — so the run breaks into several lines; KEEP-ALL demotes those
        // inter-character opportunities to Prohibited, so the run stays on ONE (overflowing) line.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("東京都", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, "Hani", "ja");

        var normal = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 10,
            wordBreak: WordBreak.Normal);
        var keepAll = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 10,
            wordBreak: WordBreak.KeepAll);

        Assert.True(normal.Length > 1, $"normal should wrap CJK per character, got {normal.Length} line(s)");
        Assert.Single(keepAll);   // keep-all keeps the ideographs together on one line
    }

    [Fact]
    public void Wrap_keep_all_still_breaks_at_a_space_between_cjk_words()
    {
        // keep-all only suppresses inter-CHARACTER opportunities; an explicit space is still a break
        // opportunity. "東京 大阪" (two 2-ideograph words separated by a space) wraps at the space under
        // keep-all (each word stays intact) rather than collapsing to a single overflowing line.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("東京 大阪", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, "Hani", "ja");

        // Width fits one 2-ideograph word (~14.4px) but not both words + space — so it wraps at the space.
        var keepAll = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 16,
            wordBreak: WordBreak.KeepAll);

        Assert.Equal(2, keepAll.Length);   // breaks at the space, one word per line
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
