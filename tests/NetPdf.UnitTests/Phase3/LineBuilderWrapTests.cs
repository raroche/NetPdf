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

    // --- Mandatory break (LF) ------------------------------------

    [Fact]
    public void Wrap_mandatory_break_at_LF_terminates_line()
    {
        // "AA\nBB" — UAX #14 Mandatory between the LF and B (LB5).
        // Available = 1000px so no soft-wrap; only the mandatory
        // break should split the input.
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
        // Two source TextRuns "AA" + "BB" → two ItemizedRuns → two
        // ShapedRuns. Available = 100px (fits everything on one
        // line). Output: one LineFragment with two Slices.
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
        // emission. The token is checked once per line emission, so
        // a pre-cancelled token throws on the first line break.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("A\nA\nA\nA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 100, cts.Token));
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
