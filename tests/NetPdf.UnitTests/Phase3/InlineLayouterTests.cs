// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
using NetPdf.Css.ComputedValues;
using NetPdf.Layout.Inline;
using NetPdf.Text.Bidi;
using NetPdf.Text.Hyphenation;
using NetPdf.Text.Shaping;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>Per Phase 3 Task 10 cycle 1 — tests for
/// <see cref="InlineLayouter"/>. The cycle 1 facade bundles
/// Itemize → Shape → Wrap into one call; tests verify the chain
/// works correctly end-to-end + each parameter is forwarded to the
/// underlying call.</summary>
public class InlineLayouterTests
{
    private const string LatnScript = "Latn";
    private const string EnLang = "en";

    // --- Arg validation ------------------------------------------

    [Fact]
    public void Layout_null_sourceTextRuns_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentNullException>(() =>
            InlineLayouter.Layout(null!, 100, resolver));
    }

    [Fact]
    public void Layout_null_resolver_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            InlineLayouter.Layout(Array.Empty<TextRun>(), 100, null!));
    }

    [Fact]
    public void Layout_null_script_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentNullException>(() =>
            InlineLayouter.Layout(Array.Empty<TextRun>(), 100, resolver,
                scriptIso15924: null!));
    }

    [Fact]
    public void Layout_null_language_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentNullException>(() =>
            InlineLayouter.Layout(Array.Empty<TextRun>(), 100, resolver,
                language: null!));
    }

    [Fact]
    public void Layout_zero_inline_size_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InlineLayouter.Layout(Array.Empty<TextRun>(), 0, resolver));
    }

    // --- Empty input ---------------------------------------------

    [Fact]
    public void Layout_empty_sourceTextRuns_returns_empty()
    {
        using var resolver = new TestShaperResolver();
        var result = InlineLayouter.Layout(
            sourceTextRuns: Array.Empty<TextRun>(),
            availableInlineSize: 100,
            resolver: resolver);
        Assert.Empty(result);
    }

    // --- End-to-end chain ----------------------------------------

    [Fact]
    public void Layout_single_run_fits_in_one_line()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 100, resolver);

        Assert.Single(result);
        var line = result[0];
        Assert.NotEmpty(line.Slices);
        var totalGlyphs = 0;
        foreach (var s in line.Slices) totalGlyphs += s.GlyphLength;
        Assert.Equal(3, totalGlyphs);
    }

    [Fact]
    public void Layout_soft_wrap_at_space()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA AAA", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 25, resolver);

        Assert.Equal(2, result.Length);
        Assert.False(result[0].EndsWithMandatoryBreak);
    }

    [Fact]
    public void Layout_mandatory_break_at_LF()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA\nAA", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 1000, resolver);

        Assert.Equal(2, result.Length);
        Assert.True(result[0].EndsWithMandatoryBreak);
    }

    [Fact]
    public void Layout_multiple_source_runs_one_line_emits_one_slice_per_run()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AA", MakeStyle()),
            new("AA", MakeStyle()),
        };
        var result = InlineLayouter.Layout(sourceRuns, 1000, resolver);

        Assert.Single(result);
        Assert.Equal(2, result[0].Slices.Length);
    }

    // --- Forwarded parameters ------------------------------------

    [Fact]
    public void Layout_NoWrap_forwarded_to_Wrap()
    {
        // NoWrap should suppress soft-wrap; even at small budget the
        // line doesn't split on spaces.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA AAA", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 25, resolver,
            whiteSpace: WhiteSpace.NoWrap);

        Assert.Single(result);
    }

    [Fact]
    public void Layout_OverflowWrap_Anywhere_forwarded()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAAA", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 15, resolver,
            overflowWrap: OverflowWrap.Anywhere);

        // Anywhere should split unbreakable run.
        Assert.True(result.Length >= 3);
    }

    [Fact]
    public void Layout_WordBreak_BreakAll_forwarded()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAAA", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 15, resolver,
            wordBreak: WordBreak.BreakAll);

        Assert.True(result.Length >= 3);
    }

    [Fact]
    public void Layout_Hyphens_Auto_forwarded()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("hyphenation", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 30, resolver,
            hyphens: Hyphens.Auto);

        // Auto + Liang patterns should split "hyphenation".
        Assert.True(result.Length >= 2);
    }

    [Fact]
    public void Layout_Hyphens_Manual_soft_hyphen_forwarded()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA­AAA", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 22, resolver,
            hyphens: Hyphens.Manual);

        Assert.Equal(2, result.Length);
        Assert.True(result[0].EndsWithHyphenationBreak);
    }

    // --- Cancellation --------------------------------------------

    [Fact]
    public void Layout_observes_cancellation()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAA", MakeStyle()) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            InlineLayouter.Layout(sourceRuns, 100, resolver,
                cancellationToken: cts.Token));
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
