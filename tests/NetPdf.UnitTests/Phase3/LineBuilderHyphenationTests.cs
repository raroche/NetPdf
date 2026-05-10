// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
using NetPdf.Layout.Inline;
using NetPdf.Text.Bidi;
using NetPdf.Text.Hyphenation;
using NetPdf.Text.Shaping;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>Per Phase 3 Task 9 cycle 3b sub-cycle 3 — tests for
/// CSS Text L3 §6.1 <c>hyphens</c> property. Two break sources:
/// <list type="bullet">
///   <item>Soft-hyphens (U+00AD) in source text — Manual + Auto.</item>
///   <item>Liang-pattern auto-hyphenation — Auto only, en-US
///   patterns via <see cref="EnUsHyphenation.Default"/>.</item>
/// </list>
/// Synthetic font lacks letter glyph variety so the test's break-
/// position arithmetic uses 'A' as a stand-in letter; the tests
/// verify that wrapping at hyphenation positions produces the
/// expected line count + glyph distribution.</summary>
public class LineBuilderHyphenationTests
{
    private const string LatnScript = "Latn";
    private const string EnLang = "en";

    // --- Arg validation ------------------------------------------

    [Fact]
    public void Wrap_undefined_hyphens_throws()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 100,
                hyphens: (Hyphens)99));
    }

    // --- Soft-hyphen (Manual + Auto) -----------------------------

    [Fact]
    public void Wrap_Manual_soft_hyphen_creates_break_opportunity()
    {
        // "AAA­AAA" with U+00AD soft-hyphen between (length 7
        // = 3 A's + SH + 3 A's). Each 'A' = 6px; SH shapes as
        // .notdef = 7.2px. Total = 25.2px. Available = 22 forces
        // wrap at the soft-hyphen position.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA­AAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 22,
            hyphens: Hyphens.Manual);

        Assert.Equal(2, lines.Length);
        Assert.False(lines[0].EndsWithMandatoryBreak);
    }

    [Fact]
    public void Wrap_None_ignores_soft_hyphen()
    {
        // Same input as above, but Hyphens.None disables soft-hyphen
        // breaks. The line should NOT wrap at the SH (since UAX #14
        // doesn't classify it as Allowed by itself in this context),
        // so the input flows as one line (overflowing).
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA­AAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 22,
            hyphens: Hyphens.None);

        Assert.Single(lines);
    }

    [Fact]
    public void Wrap_Auto_soft_hyphen_still_breaks()
    {
        // Auto includes both soft-hyphen AND Liang patterns.
        // Soft-hyphen path still works.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA­AAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 22,
            hyphens: Hyphens.Auto);

        Assert.Equal(2, lines.Length);
    }

    // --- Auto hyphenation via Liang patterns ---------------------

    [Fact]
    public void Wrap_Auto_uses_default_EnUs_hyphenator_when_null()
    {
        // Just verify the API accepts a null Hyphenator + Hyphens.Auto
        // without throwing; default en-US hyphenator activates inside.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("hyphenation", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000,
            hyphens: Hyphens.Auto);

        // Available 1000 fits the whole word; just confirm no exception.
        Assert.NotEmpty(lines);
    }

    [Fact]
    public void Wrap_Manual_does_NOT_apply_Liang_patterns()
    {
        // "hyphenation" — synthetic font has no h/y/p/n/e/i/o/t glyphs
        // so each maps to .notdef (advance 7.2px). 11 chars × 7.2 =
        // 79.2px. Available = 30: would need 3 lines under Auto +
        // Liang patterns (hy-phen-ation), but Manual with no soft-
        // hyphens in the input should NOT split (no candidate, no
        // overflow-wrap configured) — single overflowing line.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("hyphenation", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 30,
            hyphens: Hyphens.Manual);

        Assert.Single(lines);
    }

    [Fact]
    public void Wrap_Auto_applies_Liang_patterns_to_split_long_word()
    {
        // "hyphenation" with Auto + en-US patterns has hyphenation
        // points around positions 2 ("hy-") and 6 ("phen-"). At
        // available = 30px (4 letters fit at 7.2px each = 28.8),
        // Auto should snap back to one of the Liang positions.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("hyphenation", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 30,
            hyphens: Hyphens.Auto);

        // Should produce ≥2 lines (the long word splits).
        Assert.True(lines.Length >= 2,
            $"Auto + Liang patterns should split 'hyphenation'; got {lines.Length} lines.");
    }

    [Fact]
    public void Wrap_Auto_explicit_hyphenator_used()
    {
        // Pass an explicit hyphenator (the en-US default) to verify
        // the override path works.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("hyphenation", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 30,
            hyphens: Hyphens.Auto,
            hyphenator: EnUsHyphenation.Default);

        Assert.True(lines.Length >= 2);
    }

    [Fact]
    public void Wrap_Auto_short_word_below_left_right_min_no_hyphenation()
    {
        // "ab" — too short for the default leftMin=2 + rightMin=3
        // → no hyphenation positions. Should be one line.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("ab", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 1000,
            hyphens: Hyphens.Auto);

        Assert.Single(lines);
    }

    // --- Default hyphens=Manual (CSS default) ---------------------

    [Fact]
    public void Wrap_default_hyphens_is_Manual()
    {
        // Wrap without explicit hyphens param → Manual.
        // Soft-hyphen still creates a break (Manual default).
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA­AAA", MakeStyle()) };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
        var lines = LineBuilder.Wrap(sourceRuns, shaped, availableInlineSize: 22);

        Assert.Equal(2, lines.Length);
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
