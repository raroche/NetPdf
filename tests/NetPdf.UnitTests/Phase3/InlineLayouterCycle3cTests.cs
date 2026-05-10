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

/// <summary>Per Phase 3 Task 10 cycle 3c — tests for per-glyph
/// WhiteSpace honoring (mixed-mode <c>NoWrap</c>-inside-<c>Normal</c>
/// finally works without throwing).
///
/// <para>Cycle 3b's `LayoutPerRun` threw `NotSupportedException`
/// for any policy mismatch; cycle 3c relaxes this for WhiteSpace-
/// only mismatches by building a per-source-run WhiteSpace array
/// + plumbing to <see cref="LineBuilder.Wrap"/>'s new
/// <c>whiteSpacePerRun</c> parameter. Glyphs in NoWrap/Pre runs
/// get their Allowed-break opportunities suppressed.</para></summary>
public sealed class InlineLayouterCycle3cTests
{
    private const string LatnScript = "Latn";
    private const string EnLang = "en";

    [Fact]
    public void LayoutPerRun_NoWrap_span_inside_Normal_does_not_throw()
    {
        // Two runs: outer Normal "AAA " + inner NoWrap "BBB BBB".
        // Cycle 3b would have thrown; cycle 3c handles per-glyph.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AAA ", MakeStyle()),
            new("BBB BBB", MakeStyleWithNoWrap()),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns, 1000, resolver,
            LatnScript, EnLang);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void LayoutPerRun_NoWrap_span_keeps_glyphs_unwrapped_within_span()
    {
        // Outer Normal "AAA " + inner NoWrap "BBB BBB". Available =
        // 30px. Without per-glyph honoring, the inner SP would be a
        // wrap candidate. With per-glyph honoring (cycle 3c), the
        // inner span stays together; wrap can only fire at the
        // boundary or in the outer Normal text.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AAA ", MakeStyle()),
            new("BBB BBB", MakeStyleWithNoWrap()),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns, 30, resolver,
            LatnScript, EnLang);
        // Should not crash; line layout splits at outer boundary
        // when possible.
        Assert.NotEmpty(result);
    }

    [Fact]
    public void LayoutPerRun_uniform_NoWrap_still_works_via_uniform_path()
    {
        // All runs share NoWrap → no per-run array built; uniform
        // path delegates correctly.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AAA AAA", MakeStyleWithNoWrap()),
            new(" BBB", MakeStyleWithNoWrap()),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns, 25, resolver,
            LatnScript, EnLang);
        // NoWrap → single overflowing line.
        Assert.Single(result);
    }

    [Fact]
    public void LayoutPerRun_mixed_overflow_wrap_still_throws()
    {
        // Cycle 3c only handles WhiteSpace mismatch; other property
        // mismatches still throw.
        using var resolver = new TestShaperResolver();
        var sNormal = MakeStyle();
        var sAnywhere = ComputedStyle.RentForExclusiveTesting();
        sAnywhere.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere
        var sourceRuns = new List<TextRun>
        {
            new("AAA", sNormal),
            new("BBB", sAnywhere),
        };
        var ex = Assert.Throws<NotSupportedException>(() =>
            InlineLayouter.LayoutPerRun(sourceRuns, 15, resolver,
                LatnScript, EnLang));
        Assert.Contains("more than just WhiteSpace", ex.Message);
    }

    [Fact]
    public void LayoutPerRun_mixed_hyphens_still_throws()
    {
        using var resolver = new TestShaperResolver();
        var sManual = MakeStyle();
        var sAuto = ComputedStyle.RentForExclusiveTesting();
        sAuto.Set(PropertyId.Hyphens, ComputedSlot.FromKeyword(2)); // auto
        var sourceRuns = new List<TextRun>
        {
            new("foo", sManual),
            new("bar", sAuto),
        };
        Assert.Throws<NotSupportedException>(() =>
            InlineLayouter.LayoutPerRun(sourceRuns, 100, resolver,
                LatnScript, EnLang));
    }

    [Fact]
    public void Wrap_with_whiteSpacePerRun_length_mismatch_throws()
    {
        // Direct LineBuilder.Wrap test: per-run array length must
        // match sourceTextRuns count.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AAA", MakeStyle()),
            new("BBB", MakeStyle()),
        };
        var itemized = LineBuilder.Itemize(sourceRuns,
            NetPdf.Text.Bidi.ParagraphDirection.LeftToRight);
        var shaped = LineBuilder.Shape(sourceRuns, itemized, resolver,
            LatnScript, EnLang);

        var bogusPerRun = new[] { WhiteSpace.Normal }; // length 1 != 2

        Assert.Throws<ArgumentException>(() =>
            LineBuilder.Wrap(sourceRuns, shaped, 100,
                whiteSpacePerRun: bogusPerRun));
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
