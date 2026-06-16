// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Threading;
using NetPdf.Css.ComputedValues;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Inline;
using NetPdf.Text.Bidi;
using NetPdf.Text.Shaping;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>Per Phase 3 Task 9 cycle 2 + post-cycle-2 review fixes —
/// tests for <see cref="LineBuilder.Shape"/>. Cycle 2 covers the
/// shaping integration: HbShaper invocation per ItemizedRun, total-
/// advance computation, source-style routing, argument validation.
/// Post-review additions cover (a) explicit script/language metadata
/// (no more silent Latn/en defaults), (b) input-range validation
/// (SourceTextRunIndex + Utf16Start/Length bounds), (c) full-buffer
/// shaping with concat-buffer-relative cluster indices, and (d)
/// CancellationToken cooperation between runs.
///
/// <para>Note: <see cref="HbShaper"/> is sealed so this test can't
/// intercept its <c>Shape</c> call to assert the script/language/
/// direction args. Those passthrough invariants are verified by
/// integration tests in cycle 3 / Task 10 once the full pipeline
/// can be exercised end-to-end with real glyph outputs.</para></summary>
public class LineBuilderShapingTests
{
    private const string LatnScript = "Latn";
    private const string EnLang = "en";

    // --- Empty / null inputs -------------------------------------

    [Fact]
    public void Shape_empty_itemized_runs_returns_empty()
    {
        using var resolver = new TestShaperResolver();
        var result = LineBuilder.Shape(
            sourceTextRuns: Array.Empty<TextRun>(),
            itemizedRuns: Array.Empty<ItemizedRun>(),
            resolver: resolver,
            scriptIso15924: LatnScript,
            language: EnLang);
        Assert.Empty(result);
    }

    [Fact]
    public void Shape_null_sourceTextRuns_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentNullException>(() =>
            LineBuilder.Shape(null!, Array.Empty<ItemizedRun>(), resolver,
                LatnScript, EnLang));
    }

    [Fact]
    public void Shape_null_itemizedRuns_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentNullException>(() =>
            LineBuilder.Shape(Array.Empty<TextRun>(), null!, resolver,
                LatnScript, EnLang));
    }

    [Fact]
    public void Shape_null_resolver_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LineBuilder.Shape(Array.Empty<TextRun>(), Array.Empty<ItemizedRun>(),
                null!, LatnScript, EnLang));
    }

    [Fact]
    public void Shape_null_script_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentNullException>(() =>
            LineBuilder.Shape(Array.Empty<TextRun>(), Array.Empty<ItemizedRun>(),
                resolver, scriptIso15924: null!, language: EnLang));
    }

    [Fact]
    public void Shape_null_language_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentNullException>(() =>
            LineBuilder.Shape(Array.Empty<TextRun>(), Array.Empty<ItemizedRun>(),
                resolver, scriptIso15924: LatnScript, language: null!));
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
        var result = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);

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
        var result = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);

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
        var result = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);

        Assert.Equal(itemized.Length, result.Length);
        for (var i = 0; i < itemized.Length; i++)
        {
            Assert.Equal(itemized[i], result[i].Source);
        }
    }

    // --- Inline-atomic boxes (inline `<img>`) --------------------

    [Fact]
    public void Shape_atomic_run_emits_one_synthetic_glyph_carrying_the_atomic()
    {
        // inline-atomic-boxes cycle — an atomic TextRun (an inline `<img>`; its text is a single
        // U+FFFC) is NOT HarfBuzz-shaped: Shape emits one synthetic glyph whose advance is the atomic's
        // used width, TotalAdvance == that width, and the ShapedRun carries the Atomic payload so the
        // painter skips the glyph + the box paints from its own fragment.
        using var resolver = new TestShaperResolver();
        var box = Box.CreateRoot(MakeStyle());
        var sourceRuns = new List<TextRun>
        {
            new("\uFFFC", MakeStyle(), new InlineAtomic(box, WidthPx: 40, HeightPx: 24)),
        };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var result = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);

        var shaped = Assert.Single(result);
        Assert.NotNull(shaped.Atomic);
        Assert.Equal(40.0, shaped.Atomic!.Value.WidthPx, precision: 4);
        Assert.Equal(24.0, shaped.Atomic!.Value.HeightPx, precision: 4);
        Assert.Equal(40.0, shaped.TotalAdvance, precision: 4);
        var glyph = Assert.Single(shaped.Glyphs);
        Assert.Equal(40.0, glyph.XAdvance, precision: 4);
        Assert.Same(box, shaped.Atomic!.Value.Box);
    }

    [Fact]
    public void Preprocess_preserves_the_atomic_payload_through_whitespace_collapse()
    {
        // inline-atomic-boxes cycle — white-space collapsing must NOT drop an atomic run's payload (a
        // `new TextRun(text, style)` reconstruction would). The atomic survives verbatim between two
        // collapsed text runs under the default `white-space: normal`.
        var box = Box.CreateRoot(MakeStyle());
        var runs = new List<TextRun>
        {
            new("Hello ", MakeStyle()),
            new("\uFFFC", MakeStyle(), new InlineAtomic(box, WidthPx: 16, HeightPx: 16)),
            new(" World", MakeStyle()),
        };
        var result = LineBuilder.PreprocessTextRuns(runs, WhiteSpace.Normal);

        Assert.Equal(3, result.Count);
        Assert.NotNull(result[1].Atomic);
        Assert.Equal(16.0, result[1].Atomic!.Value.WidthPx, precision: 4);
        Assert.Equal("\uFFFC", result[1].Text);
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
        var result = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);

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
        var result = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);

        Assert.Equal(0, result[0].Source.Utf16Start);
        Assert.Equal(2, result[1].Source.Utf16Start);
        Assert.Equal(2, result[0].Source.Utf16Length);
        Assert.Equal(2, result[1].Source.Utf16Length);
    }

    // --- Full-buffer shaping: cluster indices are concat-relative

    [Fact]
    public void Shape_glyph_cluster_indices_are_concat_buffer_relative_for_multi_run()
    {
        // Per the post-cycle-2 review fix — Shape now passes the FULL
        // concat buffer to HbShaper with (itemOffset, itemLength) so
        // cluster indices stay concat-relative across source-run
        // boundaries. Expected: run 0's cluster ∈ [0,2), run 1's
        // cluster ∈ [2,4) since run 1 starts at concat offset 2.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AA", MakeStyle()),
            new("AA", MakeStyle()),
        };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        var result = LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);

        Assert.Equal(2, result.Length);
        // Run 0: clusters in [0, 2).
        foreach (var g in result[0].Glyphs)
        {
            Assert.InRange(g.Cluster, 0, 1);
        }
        // Run 1: clusters in [2, 4) — concat-buffer relative, NOT
        // run-local (which would have produced [0, 2)).
        foreach (var g in result[1].Glyphs)
        {
            Assert.InRange(g.Cluster, 2, 3);
        }
    }

    // --- Range validation (post-review additions) ----------------

    [Fact]
    public void Shape_throws_when_SourceTextRunIndex_out_of_range()
    {
        // Mismatched lists — itemized run claims SourceTextRunIndex=5
        // but sourceTextRuns only has 1 entry. Should throw
        // ArgumentException with a clear message, not
        // IndexOutOfRangeException.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA", MakeStyle()) };
        var bogusItemized = new[]
        {
            new ItemizedRun(Utf16Start: 0, Utf16Length: 2, BidiLevel: 0, SourceTextRunIndex: 5),
        };
        var ex = Assert.Throws<ArgumentException>(() =>
            LineBuilder.Shape(sourceRuns, bogusItemized, resolver, LatnScript, EnLang));
        Assert.Contains("SourceTextRunIndex", ex.Message);
    }

    [Fact]
    public void Shape_throws_when_Utf16Start_negative()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA", MakeStyle()) };
        var bogusItemized = new[]
        {
            new ItemizedRun(Utf16Start: -1, Utf16Length: 2, BidiLevel: 0, SourceTextRunIndex: 0),
        };
        var ex = Assert.Throws<ArgumentException>(() =>
            LineBuilder.Shape(sourceRuns, bogusItemized, resolver, LatnScript, EnLang));
        Assert.Contains("Utf16Start", ex.Message);
    }

    [Fact]
    public void Shape_throws_when_Utf16_range_extends_past_concat_length()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AA", MakeStyle()) };
        // concat length is 2; this run claims [0, 100) which overflows.
        var bogusItemized = new[]
        {
            new ItemizedRun(Utf16Start: 0, Utf16Length: 100, BidiLevel: 0, SourceTextRunIndex: 0),
        };
        var ex = Assert.Throws<ArgumentException>(() =>
            LineBuilder.Shape(sourceRuns, bogusItemized, resolver, LatnScript, EnLang));
        Assert.Contains("Utf16Length", ex.Message);
    }

    // --- CancellationToken (post-review addition) ----------------

    [Fact]
    public void Shape_observes_cancelled_token_before_first_run()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AAA", MakeStyle()),
            new("BBB", MakeStyle()),
        };
        var itemized = LineBuilder.Itemize(sourceRuns, ParagraphDirection.LeftToRight);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang, cts.Token));
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

        LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);
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

        LineBuilder.Shape(sourceRuns, itemized, resolver, LatnScript, EnLang);

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
