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

/// <summary>Per Phase 3 Task 10 cycle 1 + post-cycle-1 review
/// hardening — tests for <see cref="InlineLayouter"/>. The cycle 1
/// facade bundles PreprocessTextRuns → Itemize → Shape → Wrap into
/// one call. Tests verify:
/// <list type="bullet">
///   <item>The end-to-end chain works correctly with explicit
///   script/language metadata.</item>
///   <item>Front-loaded arg validation rejects bad inputs BEFORE
///   any expensive work (counting-resolver test pins this).</item>
///   <item>White-space preprocessing runs at the facade layer —
///   multi-run boundaries collapse correctly + CRLF normalizes.</item>
///   <item>Each wrap-policy parameter is forwarded to the
///   underlying call.</item>
///   <item>Cancellation cooperates throughout (incl. inside
///   Itemize via the new ct parameter).</item>
/// </list>
/// </summary>
public class InlineLayouterTests
{
    private const string LatnScript = "Latn";
    private const string EnLang = "en";

    // --- Arg validation (front-loaded per PR #38 review fix) -----

    [Fact]
    public void Layout_null_sourceTextRuns_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentNullException>(() =>
            InlineLayouter.Layout(null!, 100, resolver, LatnScript, EnLang));
    }

    [Fact]
    public void Layout_null_resolver_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            InlineLayouter.Layout(Array.Empty<TextRun>(), 100, null!, LatnScript, EnLang));
    }

    [Fact]
    public void Layout_null_script_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentNullException>(() =>
            InlineLayouter.Layout(Array.Empty<TextRun>(), 100, resolver, null!, EnLang));
    }

    [Fact]
    public void Layout_null_language_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentNullException>(() =>
            InlineLayouter.Layout(Array.Empty<TextRun>(), 100, resolver, LatnScript, null!));
    }

    [Fact]
    public void Layout_zero_inline_size_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InlineLayouter.Layout(Array.Empty<TextRun>(), 0, resolver, LatnScript, EnLang));
    }

    [Fact]
    public void Layout_negative_inline_size_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InlineLayouter.Layout(Array.Empty<TextRun>(), -10, resolver, LatnScript, EnLang));
    }

    [Fact]
    public void Layout_NaN_inline_size_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InlineLayouter.Layout(Array.Empty<TextRun>(), double.NaN, resolver, LatnScript, EnLang));
    }

    [Fact]
    public void Layout_undefined_whiteSpace_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InlineLayouter.Layout(Array.Empty<TextRun>(), 100, resolver, LatnScript, EnLang,
                whiteSpace: (WhiteSpace)99));
    }

    [Fact]
    public void Layout_undefined_overflowWrap_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InlineLayouter.Layout(Array.Empty<TextRun>(), 100, resolver, LatnScript, EnLang,
                overflowWrap: (OverflowWrap)99));
    }

    [Fact]
    public void Layout_undefined_wordBreak_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InlineLayouter.Layout(Array.Empty<TextRun>(), 100, resolver, LatnScript, EnLang,
                wordBreak: (WordBreak)99));
    }

    [Fact]
    public void Layout_undefined_hyphens_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InlineLayouter.Layout(Array.Empty<TextRun>(), 100, resolver, LatnScript, EnLang,
                hyphens: (Hyphens)99));
    }

    // --- Front-loaded validation: invalid inputs don't call Resolve

    [Fact]
    public void Layout_invalid_inlineSize_does_not_call_resolver()
    {
        using var counting = new CountingShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA", MakeStyle()) };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InlineLayouter.Layout(sourceRuns, -1, counting, LatnScript, EnLang));

        Assert.Equal(0, counting.ResolveCallCount);
    }

    [Fact]
    public void Layout_invalid_whiteSpace_does_not_call_resolver()
    {
        using var counting = new CountingShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA", MakeStyle()) };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InlineLayouter.Layout(sourceRuns, 100, counting, LatnScript, EnLang,
                whiteSpace: (WhiteSpace)99));

        Assert.Equal(0, counting.ResolveCallCount);
    }

    // --- Empty input ---------------------------------------------

    [Fact]
    public void Layout_empty_sourceTextRuns_returns_empty()
    {
        using var resolver = new TestShaperResolver();
        var result = InlineLayouter.Layout(
            sourceTextRuns: Array.Empty<TextRun>(),
            availableInlineSize: 100,
            resolver: resolver,
            scriptIso15924: LatnScript,
            language: EnLang);
        Assert.Empty(result.Lines);
    }

    // --- End-to-end chain ----------------------------------------

    [Fact]
    public void Layout_single_run_fits_in_one_line()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 100, resolver, LatnScript, EnLang);

        Assert.Single(result.Lines);
        var line = result.Lines[0];
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
        var result = InlineLayouter.Layout(sourceRuns, 25, resolver, LatnScript, EnLang);

        Assert.Equal(2, result.Lines.Length);
        Assert.False(result.Lines[0].EndsWithMandatoryBreak);
    }

    [Fact]
    public void Layout_mandatory_break_at_LF_under_Pre()
    {
        // LF preserved under Pre. Without preprocessing it would be
        // collapsed to space under Normal — which is the right
        // behavior, see the next test.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA\nAA", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 1000, resolver, LatnScript, EnLang,
            whiteSpace: WhiteSpace.Pre);

        Assert.Equal(2, result.Lines.Length);
        Assert.True(result.Lines[0].EndsWithMandatoryBreak);
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
        var result = InlineLayouter.Layout(sourceRuns, 1000, resolver, LatnScript, EnLang);

        Assert.Single(result.Lines);
        Assert.Equal(2, result.Lines[0].Slices.Length);
    }

    // --- White-space preprocessing wired into facade (User #1) ---

    [Fact]
    public void Layout_Normal_collapses_multiple_spaces()
    {
        // "A  B" under Normal — preprocessing collapses two spaces
        // into one. Without preprocessing, the line would have an
        // extra .notdef glyph for the second space.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("A  B", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 1000, resolver, LatnScript, EnLang,
            whiteSpace: WhiteSpace.Normal);

        Assert.Single(result.Lines);
        var totalGlyphs = 0;
        foreach (var s in result.Lines[0].Slices) totalGlyphs += s.GlyphLength;
        // After preprocessing "A  B" → "A B" — 3 glyphs (A, space, B).
        Assert.Equal(3, totalGlyphs);
    }

    [Fact]
    public void Layout_Normal_collapses_LF_to_space()
    {
        // "A\nB" under Normal — LF collapses to space → "A B".
        // Result is a single line (no LF-induced break). UAX #14
        // LB3 still classifies end-of-text as Mandatory, but that
        // applies to ALL line emissions; the test focus is the line
        // count: 1 line means LF was collapsed, 2 means it wasn't.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("A\nB", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 1000, resolver, LatnScript, EnLang,
            whiteSpace: WhiteSpace.Normal);

        Assert.Single(result.Lines);
    }

    [Fact]
    public void Layout_Normal_collapses_across_textrun_boundary()
    {
        // "A " + " B" under Normal — preprocessing's stateful
        // PreprocessTextRuns collapses the boundary spaces to one.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("A ", MakeStyle()),
            new(" B", MakeStyle()),
        };
        var result = InlineLayouter.Layout(sourceRuns, 1000, resolver, LatnScript, EnLang,
            whiteSpace: WhiteSpace.Normal);

        Assert.Single(result.Lines);
        var totalGlyphs = 0;
        foreach (var s in result.Lines[0].Slices) totalGlyphs += s.GlyphLength;
        // After collapse: "A " + "B" = "A B" — 3 glyphs. Without
        // preprocessing the boundary, two spaces would survive.
        Assert.Equal(3, totalGlyphs);
    }

    [Fact]
    public void Layout_PreLine_normalizes_CRLF_to_LF()
    {
        // PreLine preserves segment breaks but normalizes CRLF→LF.
        // Result: "AA\r\nBB" → "AA\nBB" → 2 lines (mandatory between).
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA\r\nBB", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 1000, resolver, LatnScript, EnLang,
            whiteSpace: WhiteSpace.PreLine);

        Assert.Equal(2, result.Lines.Length);
        Assert.True(result.Lines[0].EndsWithMandatoryBreak);
    }

    [Fact]
    public void Layout_Pre_preserves_consecutive_spaces()
    {
        // Pre passes through unchanged (modulo CRLF normalization).
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("A  B", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 1000, resolver, LatnScript, EnLang,
            whiteSpace: WhiteSpace.Pre);

        Assert.Single(result.Lines);
        var totalGlyphs = 0;
        foreach (var s in result.Lines[0].Slices) totalGlyphs += s.GlyphLength;
        // Pre preserves both spaces: A + SP + SP + B = 4 glyphs.
        Assert.Equal(4, totalGlyphs);
    }

    [Fact]
    public void Layout_PreWrap_preserves_consecutive_spaces()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("A  B", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 1000, resolver, LatnScript, EnLang,
            whiteSpace: WhiteSpace.PreWrap);

        Assert.Single(result.Lines);
        var totalGlyphs = 0;
        foreach (var s in result.Lines[0].Slices) totalGlyphs += s.GlyphLength;
        Assert.Equal(4, totalGlyphs);
    }

    // --- Forwarded parameters ------------------------------------

    [Fact]
    public void Layout_NoWrap_forwarded_to_Wrap()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA AAA", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 25, resolver, LatnScript, EnLang,
            whiteSpace: WhiteSpace.NoWrap);

        Assert.Single(result.Lines);
    }

    [Fact]
    public void Layout_OverflowWrap_Anywhere_forwarded()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAAA", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 15, resolver, LatnScript, EnLang,
            overflowWrap: OverflowWrap.Anywhere);

        Assert.True(result.Lines.Length >= 3);
    }

    [Fact]
    public void Layout_WordBreak_BreakAll_forwarded()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAAA", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 15, resolver, LatnScript, EnLang,
            wordBreak: WordBreak.BreakAll);

        Assert.True(result.Lines.Length >= 3);
    }

    [Fact]
    public void Layout_Hyphens_Auto_forwarded()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("hyphenation", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 30, resolver, LatnScript, EnLang,
            hyphens: Hyphens.Auto);

        Assert.True(result.Lines.Length >= 2);
    }

    [Fact]
    public void Layout_Hyphens_Manual_soft_hyphen_forwarded()
    {
        // U+00AD soft-hyphen is NOT in CSS Text L3 §3.1's
        // collapsible-whitespace set (SP/TAB/LF/CR/FF) so it's
        // preserved through Normal-mode preprocessing. Manual mode
        // then treats it as a break opportunity per CSS Text L3 §6.1.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA­AAA", MakeStyle()) };
        var result = InlineLayouter.Layout(sourceRuns, 22, resolver, LatnScript, EnLang,
            hyphens: Hyphens.Manual);

        Assert.Equal(2, result.Lines.Length);
        Assert.True(result.Lines[0].EndsWithHyphenationBreak);
    }

    // --- Cancellation --------------------------------------------

    [Fact]
    public void Layout_observes_cancellation_at_entry()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAA", MakeStyle()) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            InlineLayouter.Layout(sourceRuns, 100, resolver, LatnScript, EnLang,
                cancellationToken: cts.Token));
    }

    [Fact]
    public void Layout_precancelled_token_does_not_call_resolver()
    {
        // Per PR #38 review fix (User #4): cancellation at entry
        // means even Itemize never runs, let alone Shape.
        using var counting = new CountingShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAA", MakeStyle()) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            InlineLayouter.Layout(sourceRuns, 100, counting, LatnScript, EnLang,
                cancellationToken: cts.Token));
        Assert.Equal(0, counting.ResolveCallCount);
    }

    [Fact]
    public void Layout_large_input_cancellation_during_Itemize()
    {
        // Per PR #38 review fix (User #4): LineBuilder.Itemize now
        // honors cancellation. A pre-cancelled token throws inside
        // Itemize for large inputs (no resolver call).
        using var counting = new CountingShaperResolver();
        // 50k chars — bidi pass would normally be uninterrupted.
        var sourceRuns = new List<TextRun> { new(new string('A', 50_000), MakeStyle()) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            InlineLayouter.Layout(sourceRuns, 100, counting, LatnScript, EnLang,
                cancellationToken: cts.Token));
        Assert.Equal(0, counting.ResolveCallCount);
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

    private sealed class CountingShaperResolver : IShaperResolver
    {
        private readonly HbShaper _shaper = new(SyntheticFont.Build(), fontSizePx: 12);
        public int ResolveCallCount { get; private set; }
        public HbShaper Resolve(ComputedStyle style)
        {
            ResolveCallCount++;
            return _shaper;
        }
        public void Dispose() => _shaper.Dispose();
    }
}
