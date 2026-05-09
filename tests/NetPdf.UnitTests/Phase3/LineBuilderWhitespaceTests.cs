// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
using NetPdf.Layout.Inline;
using NetPdf.Text.Bidi;
using NetPdf.Text.Shaping;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>Per Phase 3 Task 9 cycle 3b sub-cycle 1 — tests for
/// CSS Text L3 <c>white-space</c> handling. Two surfaces under test:
/// <list type="bullet">
///   <item><see cref="LineBuilder.PreprocessWhitespace"/> — pre-
///   shaping text transformation (collapse / preserve per mode).</item>
///   <item><see cref="LineBuilder.Wrap"/>'s new
///   <see cref="WhiteSpace"/> parameter — wrap-time semantics
///   (Pre + NoWrap suppress Allowed-break wrapping).</item>
/// </list></summary>
public class LineBuilderWhitespaceTests
{
    private const string LatnScript = "Latn";
    private const string EnLang = "en";

    // --- PreprocessWhitespace: arg validation -------------------

    [Fact]
    public void PreprocessWhitespace_null_text_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LineBuilder.PreprocessWhitespace(null!, WhiteSpace.Normal));
    }

    [Fact]
    public void PreprocessWhitespace_undefined_mode_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LineBuilder.PreprocessWhitespace("AA", (WhiteSpace)99));
    }

    [Fact]
    public void PreprocessWhitespace_empty_text_returns_empty()
    {
        Assert.Equal("", LineBuilder.PreprocessWhitespace("", WhiteSpace.Normal));
        Assert.Equal("", LineBuilder.PreprocessWhitespace("", WhiteSpace.Pre));
        Assert.Equal("", LineBuilder.PreprocessWhitespace("", WhiteSpace.NoWrap));
        Assert.Equal("", LineBuilder.PreprocessWhitespace("", WhiteSpace.PreWrap));
        Assert.Equal("", LineBuilder.PreprocessWhitespace("", WhiteSpace.PreLine));
    }

    // --- PreprocessWhitespace: Normal --------------------------

    [Fact]
    public void PreprocessWhitespace_Normal_collapses_consecutive_spaces()
    {
        Assert.Equal("AA AA", LineBuilder.PreprocessWhitespace("AA  AA", WhiteSpace.Normal));
        Assert.Equal("AA AA", LineBuilder.PreprocessWhitespace("AA   AA", WhiteSpace.Normal));
        Assert.Equal("AA AA", LineBuilder.PreprocessWhitespace("AA \t \t AA", WhiteSpace.Normal));
    }

    [Fact]
    public void PreprocessWhitespace_Normal_collapses_LF_to_space()
    {
        Assert.Equal("AA AA", LineBuilder.PreprocessWhitespace("AA\nAA", WhiteSpace.Normal));
        Assert.Equal("AA AA", LineBuilder.PreprocessWhitespace("AA\r\nAA", WhiteSpace.Normal));
        Assert.Equal("AA AA", LineBuilder.PreprocessWhitespace("AA \n\n AA", WhiteSpace.Normal));
    }

    [Fact]
    public void PreprocessWhitespace_Normal_strips_leading_whitespace()
    {
        Assert.Equal("AA", LineBuilder.PreprocessWhitespace("  AA", WhiteSpace.Normal));
        Assert.Equal("AA", LineBuilder.PreprocessWhitespace("\t\nAA", WhiteSpace.Normal));
    }

    [Fact]
    public void PreprocessWhitespace_Normal_strips_trailing_whitespace()
    {
        Assert.Equal("AA", LineBuilder.PreprocessWhitespace("AA  ", WhiteSpace.Normal));
        Assert.Equal("AA", LineBuilder.PreprocessWhitespace("AA\t\n", WhiteSpace.Normal));
    }

    [Fact]
    public void PreprocessWhitespace_Normal_pure_whitespace_collapses_to_empty()
    {
        Assert.Equal("", LineBuilder.PreprocessWhitespace("   ", WhiteSpace.Normal));
        Assert.Equal("", LineBuilder.PreprocessWhitespace(" \t\n\r ", WhiteSpace.Normal));
    }

    // --- PreprocessWhitespace: Pre / PreWrap (pass-through) ----

    [Fact]
    public void PreprocessWhitespace_Pre_passes_through_unchanged()
    {
        Assert.Equal("AA  AA\nBB", LineBuilder.PreprocessWhitespace("AA  AA\nBB", WhiteSpace.Pre));
        Assert.Equal("  AA  ", LineBuilder.PreprocessWhitespace("  AA  ", WhiteSpace.Pre));
    }

    [Fact]
    public void PreprocessWhitespace_PreWrap_passes_through_unchanged()
    {
        Assert.Equal("AA  AA\nBB", LineBuilder.PreprocessWhitespace("AA  AA\nBB", WhiteSpace.PreWrap));
    }

    // --- PreprocessWhitespace: NoWrap (collapses like Normal) --

    [Fact]
    public void PreprocessWhitespace_NoWrap_collapses_like_Normal()
    {
        Assert.Equal("AA AA",
            LineBuilder.PreprocessWhitespace("AA  AA", WhiteSpace.NoWrap));
        Assert.Equal("AA AA",
            LineBuilder.PreprocessWhitespace("AA\nAA", WhiteSpace.NoWrap));
    }

    // --- PreprocessWhitespace: PreLine -------------------------

    [Fact]
    public void PreprocessWhitespace_PreLine_collapses_spaces_preserves_LF()
    {
        Assert.Equal("AA AA\nBB",
            LineBuilder.PreprocessWhitespace("AA  AA\nBB", WhiteSpace.PreLine));
        Assert.Equal("AA\nBB",
            LineBuilder.PreprocessWhitespace("AA\nBB", WhiteSpace.PreLine));
    }

    [Fact]
    public void PreprocessWhitespace_PreLine_strips_trailing_space_at_segment_end()
    {
        // "AA   \nBB" — three trailing spaces before LF should strip
        // per CSS Text L3 §4.1.2 "remove end-of-line spaces".
        Assert.Equal("AA\nBB",
            LineBuilder.PreprocessWhitespace("AA   \nBB", WhiteSpace.PreLine));
    }

    [Fact]
    public void PreprocessWhitespace_PreLine_preserves_CRLF()
    {
        // PreLine preserves both CR + LF.
        Assert.Equal("AA\r\nBB",
            LineBuilder.PreprocessWhitespace("AA\r\nBB", WhiteSpace.PreLine));
    }

    // --- Wrap with WhiteSpace.NoWrap suppresses Allowed wrap ---

    [Fact]
    public void Wrap_NoWrap_ignores_Allowed_break_keeps_one_overflowing_line()
    {
        // "AA AA AA" with available=10px would normally wrap at each
        // space (UAX #14 LB18 Allowed). NoWrap suppresses Allowed
        // breaks — all glyphs flow into one overflowing line.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA AA AA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 10,
            whiteSpace: WhiteSpace.NoWrap);

        Assert.Single(lines);
    }

    [Fact]
    public void Wrap_NoWrap_still_honors_Mandatory_break()
    {
        // NoWrap doesn't suppress Mandatory — LF still produces a
        // line break.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA AA\nAA AA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000,
            whiteSpace: WhiteSpace.NoWrap);

        Assert.Equal(2, lines.Length);
        Assert.True(lines[0].EndsWithMandatoryBreak);
    }

    [Fact]
    public void Wrap_Pre_ignores_Allowed_break_keeps_one_overflowing_line()
    {
        // Pre suppresses Allowed-break wrapping like NoWrap.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA AA AA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 10,
            whiteSpace: WhiteSpace.Pre);

        Assert.Single(lines);
    }

    [Fact]
    public void Wrap_PreWrap_wraps_at_Allowed_breaks()
    {
        // PreWrap allows wrapping at Allowed UAX #14 opportunities
        // (between spaces). Should wrap at the space.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA AAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 25,
            whiteSpace: WhiteSpace.PreWrap);

        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void Wrap_PreLine_wraps_at_Allowed_breaks()
    {
        // PreLine wraps like Normal/PreWrap (preserves LF separately).
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA AAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 25,
            whiteSpace: WhiteSpace.PreLine);

        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void Wrap_undefined_whiteSpace_throws()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 100,
                whiteSpace: (WhiteSpace)99));
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
