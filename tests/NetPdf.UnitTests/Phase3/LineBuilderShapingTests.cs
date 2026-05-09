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

/// <summary>Per Phase 3 Task 9 cycle 2 — tests for
/// <see cref="LineBuilder.Shape"/>. Cycle 2 covers the shaping
/// integration: HbShaper invocation per ItemizedRun, total-advance
/// computation, source-style routing, + argument validation. Cycle
/// 3 will add line-break + wrap tests that build on this output.
///
/// <para>Note: <see cref="HbShaper"/> is sealed so this test can't
/// intercept its <c>Shape</c> call to assert the script/language/
/// direction args. Those passthrough invariants are verified by
/// integration tests in cycle 3 / Task 10 once the full pipeline
/// can be exercised end-to-end with real glyph outputs.</para></summary>
public class LineBuilderShapingTests
{
    // --- Empty / null inputs -------------------------------------

    [Fact]
    public void Shape_empty_itemized_runs_returns_empty()
    {
        using var resolver = new TestShaperResolver();
        var result = LineBuilder.Shape(
            sourceTextRuns: Array.Empty<TextRun>(),
            itemizedRuns: Array.Empty<ItemizedRun>(),
            resolver: resolver);
        Assert.Empty(result);
    }

    [Fact]
    public void Shape_null_sourceTextRuns_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentNullException>(() =>
            LineBuilder.Shape(null!, Array.Empty<ItemizedRun>(), resolver));
    }

    [Fact]
    public void Shape_null_itemizedRuns_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentNullException>(() =>
            LineBuilder.Shape(Array.Empty<TextRun>(), null!, resolver));
    }

    [Fact]
    public void Shape_null_resolver_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LineBuilder.Shape(Array.Empty<TextRun>(), Array.Empty<ItemizedRun>(), null!));
    }

    [Fact]
    public void Shape_null_script_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentNullException>(() =>
            LineBuilder.Shape(Array.Empty<TextRun>(), Array.Empty<ItemizedRun>(),
                resolver, scriptIso15924: null!));
    }

    [Fact]
    public void Shape_null_language_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentNullException>(() =>
            LineBuilder.Shape(Array.Empty<TextRun>(), Array.Empty<ItemizedRun>(),
                resolver, language: null!));
    }

    // --- LTR shaping roundtrip -----------------------------------

    [Fact]
    public void Shape_single_LTR_run_produces_glyphs_with_positive_advance()
    {
        // Synthetic font has glyph-1 mapped from 'A' (square 0..500).
        // Three 'A's → 3 glyphs, each XAdvance > 0.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AAA", MakeStyle()),
        };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var result = LineBuilder.Shape(sourceRuns, itemized, resolver);

        Assert.Single(result);
        var shaped = result[0];
        Assert.Equal(3, shaped.Glyphs.Length);
        Assert.True(shaped.TotalAdvance > 0,
            $"Expected positive total advance, got {shaped.TotalAdvance}");
        // Each glyph should have non-zero XAdvance for the 'A' glyph.
        foreach (var g in shaped.Glyphs)
        {
            Assert.True(g.XAdvance > 0,
                $"Each glyph should have positive XAdvance, got {g.XAdvance}");
        }
    }

    [Fact]
    public void Shape_total_advance_equals_sum_of_glyph_advances()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AAAAA", MakeStyle()),
        };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var result = LineBuilder.Shape(sourceRuns, itemized, resolver);

        var shaped = result[0];
        double summedAdvance = 0;
        foreach (var g in shaped.Glyphs) summedAdvance += g.XAdvance;
        Assert.Equal(summedAdvance, shaped.TotalAdvance, precision: 4);
    }

    // --- Source itemization preserved ----------------------------

    [Fact]
    public void Shape_source_itemized_run_preserved_on_output()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AA", MakeStyle()),
            new("BB", MakeStyle()),
        };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var result = LineBuilder.Shape(sourceRuns, itemized, resolver);

        Assert.Equal(itemized.Length, result.Length);
        for (var i = 0; i < itemized.Length; i++)
        {
            Assert.Equal(itemized[i], result[i].Source);
        }
    }

    // --- Multi-run inputs ----------------------------------------

    [Fact]
    public void Shape_multiple_source_runs_each_resolves_to_a_shaped_run()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("A", MakeStyle()),
            new("AA", MakeStyle()),
            new("AAA", MakeStyle()),
        };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var result = LineBuilder.Shape(sourceRuns, itemized, resolver);

        Assert.Equal(3, result.Length);
        Assert.Single(result[0].Glyphs);
        Assert.Equal(2, result[1].Glyphs.Length);
        Assert.Equal(3, result[2].Glyphs.Length);
    }

    // --- Source.Utf16Start preserved -----------------------------

    [Fact]
    public void Shape_source_Utf16Start_offsets_are_concat_text_relative()
    {
        // For "AA" (run 0) + "AA" (run 1), the second itemized run's
        // Utf16Start = 2 in concatenated coords.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AA", MakeStyle()),
            new("AA", MakeStyle()),
        };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var result = LineBuilder.Shape(sourceRuns, itemized, resolver);

        Assert.Equal(0, result[0].Source.Utf16Start);
        Assert.Equal(2, result[1].Source.Utf16Start);
        Assert.Equal(2, result[0].Source.Utf16Length);
        Assert.Equal(2, result[1].Source.Utf16Length);
    }

    // --- Resolver invoked per run --------------------------------

    [Fact]
    public void Shape_resolver_invoked_once_per_itemized_run()
    {
        using var resolver = new CountingShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AAA", MakeStyle()),
            new("AAA", MakeStyle()),
        };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        Assert.Equal(2, itemized.Length);

        LineBuilder.Shape(sourceRuns, itemized, resolver);
        Assert.Equal(2, resolver.ResolveCallCount);
    }

    [Fact]
    public void Shape_resolver_passed_correct_styles_per_run()
    {
        // Two source TextRuns with distinguishable styles → resolver
        // is called with each style in source order.
        using var resolver = new StyleRecordingResolver();
        var style1 = MakeStyle();
        var style2 = MakeStyle();

        var sourceRuns = new List<TextRun>
        {
            new("A", style1),
            new("A", style2),
        };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        Assert.Equal(2, itemized.Length);

        LineBuilder.Shape(sourceRuns, itemized, resolver);

        Assert.Equal(2, resolver.RecordedStyles.Count);
        Assert.Same(style1, resolver.RecordedStyles[0]);
        Assert.Same(style2, resolver.RecordedStyles[1]);
    }

    // --- Helpers --------------------------------------------------

    private static ComputedStyle MakeStyle() =>
        ComputedStyle.RentForExclusiveTesting();

    /// <summary>Test resolver: a single shaper backed by the
    /// synthetic font. Returned for any style.</summary>
    private sealed class TestShaperResolver : IShaperResolver
    {
        private readonly HbShaper _shaper = new(SyntheticFont.Build(), fontSizePx: 12);
        public HbShaper Resolve(ComputedStyle style) => _shaper;
        public void Dispose() => _shaper.Dispose();
    }

    /// <summary>Test resolver that counts <see cref="Resolve"/>
    /// invocations.</summary>
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

    /// <summary>Test resolver that records each style passed to
    /// <see cref="Resolve"/>. Verifies LineBuilder.Shape routes the
    /// right source style to each itemized run.</summary>
    private sealed class StyleRecordingResolver : IShaperResolver
    {
        private readonly HbShaper _shaper = new(SyntheticFont.Build(), fontSizePx: 12);
        public List<ComputedStyle> RecordedStyles { get; } = new();
        public HbShaper Resolve(ComputedStyle style)
        {
            RecordedStyles.Add(style);
            return _shaper;
        }
        public void Dispose() => _shaper.Dispose();
    }
}
