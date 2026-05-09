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
    public void PreprocessWhitespace_PreLine_normalizes_CRLF_to_LF()
    {
        // Per CSS Text L3 §4.1.1 — CRLF → single LF, lone CR → LF.
        // PreLine preserves segment breaks (LF) but normalizes CRLF
        // and lone CR to LF first.
        Assert.Equal("AA\nBB",
            LineBuilder.PreprocessWhitespace("AA\r\nBB", WhiteSpace.PreLine));
        Assert.Equal("AA\nBB",
            LineBuilder.PreprocessWhitespace("AA\rBB", WhiteSpace.PreLine));
    }

    [Fact]
    public void PreprocessWhitespace_Pre_normalizes_CRLF_to_LF()
    {
        // Per CSS Text L3 §4.1.1 — Pre also normalizes CRLF/CR to LF
        // (otherwise renderers would treat CR + LF as TWO segment breaks).
        Assert.Equal("AA\nBB",
            LineBuilder.PreprocessWhitespace("AA\r\nBB", WhiteSpace.Pre));
        Assert.Equal("AA\nBB\nCC",
            LineBuilder.PreprocessWhitespace("AA\rBB\rCC", WhiteSpace.Pre));
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

    // --- PreprocessTextRuns: inline-context state ----------------

    [Fact]
    public void PreprocessTextRuns_null_runs_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LineBuilder.PreprocessTextRuns(null!, WhiteSpace.Normal));
    }

    [Fact]
    public void PreprocessTextRuns_empty_returns_empty()
    {
        var result = LineBuilder.PreprocessTextRuns(
            Array.Empty<TextRun>(), WhiteSpace.Normal);
        Assert.Empty(result);
    }

    [Fact]
    public void PreprocessTextRuns_undefined_mode_throws()
    {
        var runs = new List<TextRun> { new("AA", MakeStyle()) };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LineBuilder.PreprocessTextRuns(runs, (WhiteSpace)99));
    }

    [Fact]
    public void PreprocessTextRuns_Normal_carries_collapse_state_across_runs()
    {
        // "Hello " + "world" — naive per-run preprocessing would
        // strip trailing SP from run 1 + give run 2 unchanged →
        // concat = "Helloworld" (missing space). Stateful
        // preprocessing produces "Hello " + "world" (the SP is
        // preserved on run 1 since run 2 is non-empty).
        var s1 = MakeStyle();
        var s2 = MakeStyle();
        var runs = new List<TextRun>
        {
            new("Hello ", s1),
            new("world", s2),
        };
        var result = LineBuilder.PreprocessTextRuns(runs, WhiteSpace.Normal);

        Assert.Equal(2, result.Count);
        Assert.Equal("Hello ", result[0].Text);
        Assert.Equal("world", result[1].Text);
        Assert.Same(s1, result[0].Style);
        Assert.Same(s2, result[1].Style);
    }

    [Fact]
    public void PreprocessTextRuns_Normal_collapses_trailing_and_leading_at_boundary()
    {
        // "Hello " + " world" — both have whitespace at the boundary.
        // Stateful preprocessing collapses them to a single SP:
        // run 1 emits "Hello " (with trailing SP, inWs=true at end);
        // run 2 sees " world" — leading SP is dropped (inWs=true on
        // entry), 'w' starts new word: "world".
        var runs = new List<TextRun>
        {
            new("Hello ", MakeStyle()),
            new(" world", MakeStyle()),
        };
        var result = LineBuilder.PreprocessTextRuns(runs, WhiteSpace.Normal);

        Assert.Equal("Hello ", result[0].Text);
        Assert.Equal("world", result[1].Text);
    }

    [Fact]
    public void PreprocessTextRuns_Normal_strips_document_leading_and_trailing()
    {
        // "  Hello " + "world  " — document-leading + trailing both
        // strip; the trailing SP of "Hello " is preserved (run 2 is
        // non-empty, but run 2's trailing is stripped at the end).
        var runs = new List<TextRun>
        {
            new("  Hello ", MakeStyle()),
            new("world  ", MakeStyle()),
        };
        var result = LineBuilder.PreprocessTextRuns(runs, WhiteSpace.Normal);

        Assert.Equal("Hello ", result[0].Text);
        Assert.Equal("world", result[1].Text); // doc-trailing stripped
    }

    [Fact]
    public void PreprocessTextRuns_Normal_styled_leading_space_collapses_to_none()
    {
        // "Hello" + " world" — run 1 has no trailing SP, so the SP
        // at the start of run 2 IS the inter-run space. Result:
        // "Hello" + " world".
        var runs = new List<TextRun>
        {
            new("Hello", MakeStyle()),
            new(" world", MakeStyle()),
        };
        var result = LineBuilder.PreprocessTextRuns(runs, WhiteSpace.Normal);

        Assert.Equal("Hello", result[0].Text);
        Assert.Equal(" world", result[1].Text);
    }

    [Fact]
    public void PreprocessTextRuns_Pre_normalizes_segment_breaks_per_run()
    {
        // Pre passes through with §4.1.1 normalization (CRLF → LF).
        var runs = new List<TextRun>
        {
            new("Hello\r\n", MakeStyle()),
            new("world", MakeStyle()),
        };
        var result = LineBuilder.PreprocessTextRuns(runs, WhiteSpace.Pre);

        Assert.Equal("Hello\n", result[0].Text);
        Assert.Equal("world", result[1].Text);
    }

    // --- Soft-wrap break-space trim (User #1) --------------------

    [Fact]
    public void Wrap_Normal_soft_wrap_trims_break_space_glyph_from_drawable()
    {
        // Synthetic font's .notdef has advance 600 fontUnits = 7.2px
        // at 12pt unitsPerEm 1000. SP shapes as .notdef.
        // "AAA AAA" with available=20 wraps at the SP.
        // Without trim: line 1 = AAA + SP = 4 glyphs, advance 25.2.
        // With trim:    line 1 = AAA      = 3 glyphs, advance 18.0.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA AAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 20,
            whiteSpace: WhiteSpace.Normal);

        Assert.Equal(2, lines.Length);
        var line1Glyphs = 0;
        double line1Advance = 0;
        foreach (var s in lines[0].Slices)
        {
            line1Glyphs += s.GlyphLength;
            line1Advance += s.SliceAdvance;
        }
        Assert.Equal(3, line1Glyphs); // SP trimmed
        Assert.True(line1Advance < 20.0,
            $"Line 1 advance after trim should be < 20px, got {line1Advance}");
    }

    [Fact]
    public void Wrap_PreLine_soft_wrap_trims_break_space_glyph()
    {
        // PreLine collapses SP/TAB so it ALSO trims break-space at
        // soft-wrap. Same input + assertion as Normal.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA AAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 20,
            whiteSpace: WhiteSpace.PreLine);

        Assert.Equal(2, lines.Length);
        var line1Glyphs = 0;
        foreach (var s in lines[0].Slices) line1Glyphs += s.GlyphLength;
        Assert.Equal(3, line1Glyphs); // SP trimmed
    }

    [Fact]
    public void Wrap_PreWrap_soft_wrap_does_NOT_trim_break_space()
    {
        // PreWrap preserves spaces — break-space at the wrap point
        // IS part of the rendered output. Line 1 should INCLUDE
        // the SP glyph (4 glyphs total: "AAA ").
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA AAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 20,
            whiteSpace: WhiteSpace.PreWrap);

        Assert.Equal(2, lines.Length);
        var line1Glyphs = 0;
        foreach (var s in lines[0].Slices) line1Glyphs += s.GlyphLength;
        Assert.Equal(4, line1Glyphs); // SP NOT trimmed (preserve mode)
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
