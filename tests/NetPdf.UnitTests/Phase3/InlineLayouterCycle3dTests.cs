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
    public void LayoutPerRun_mixed_overflow_wrap_still_throws()
    {
        // Cycle 3d sub-cycle 1 broadens the WhiteSpace matrix; the
        // OTHER 3 properties (overflow-wrap, word-break, hyphens)
        // still throw on mismatch (per-glyph metadata for those
        // deferred to a subsequent cycle).
        using var resolver = new TestShaperResolver();
        var sNormal = MakeStyle();
        var sAnywhere = ComputedStyle.RentForExclusiveTesting();
        sAnywhere.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere
        var sourceRuns = new List<TextRun>
        {
            new("AAA", sNormal),
            new("BBB", sAnywhere),
        };
        Assert.Throws<NotSupportedException>(() =>
            InlineLayouter.LayoutPerRun(sourceRuns, 100, resolver,
                LatnScript, EnLang));
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
