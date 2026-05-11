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

/// <summary>Per Phase 3 Task 10 cycle 3b — tests for
/// <see cref="InlineLayouter.LayoutPerRun"/>. Cycle 3b reads each
/// source-TextRun's policy + verifies they all match. Mixed-mode
/// throws <see cref="NotSupportedException"/> until cycle 3c lands
/// per-glyph metadata.</summary>
public sealed class InlineLayouterCycle3bTests
{
    private const string LatnScript = "Latn";
    private const string EnLang = "en";

    [Fact]
    public void LayoutPerRun_null_sourceTextRuns_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentNullException>(() =>
            InlineLayouter.LayoutPerRun(null!, 100, resolver, LatnScript, EnLang));
    }

    [Fact]
    public void LayoutPerRun_empty_returns_empty()
    {
        using var resolver = new TestShaperResolver();
        var result = InlineLayouter.LayoutPerRun(
            Array.Empty<TextRun>(), 100, resolver, LatnScript, EnLang);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public void LayoutPerRun_uniform_policy_runs_layout_normally()
    {
        // All runs share default policy (Normal/Normal/Normal/Manual)
        // → uniform path delegates correctly.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AAA", MakeStyle()),
            new(" ", MakeStyle()),
            new("AAA", MakeStyle()),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns, 1000, resolver,
            LatnScript, EnLang);
        Assert.Single(result.Lines);
    }

    [Fact]
    public void LayoutPerRun_uniform_NoWrap_runs_correctly()
    {
        // All runs share NoWrap policy → suppress wrapping.
        using var resolver = new TestShaperResolver();
        var s1 = MakeStyleWithNoWrap();
        var s2 = MakeStyleWithNoWrap();
        var sourceRuns = new List<TextRun>
        {
            new("AAA AAA", s1),
            new(" BBB", s2),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns, 25, resolver,
            LatnScript, EnLang);
        // NoWrap → single overflowing line (would be 2 lines under Normal).
        Assert.Single(result.Lines);
    }

    [Fact]
    public void LayoutPerRun_mixed_white_space_handled_via_per_glyph_path()
    {
        // Per Phase 3 Task 10 cycle 3c — mixed-mode WhiteSpace
        // (NoWrap span inside Normal text) is now handled via the
        // per-glyph WhiteSpace honoring path. Cycle 3b would have
        // thrown NotSupportedException; cycle 3c builds a per-run
        // WhiteSpace array + delegates to LineBuilder.Wrap with the
        // array. Glyphs in NoWrap runs get their Allowed
        // opportunities suppressed.
        using var resolver = new TestShaperResolver();
        var sNormal = MakeStyle();
        var sNoWrap = MakeStyleWithNoWrap();
        var sourceRuns = new List<TextRun>
        {
            new("AAA", sNormal),
            new(" BBB", sNoWrap),
        };
        // Should not throw; result depends on wrap logic.
        var result = InlineLayouter.LayoutPerRun(sourceRuns, 100, resolver,
            LatnScript, EnLang);
        Assert.NotEmpty(result.Lines);
    }

    [Fact]
    public void LayoutPerRun_mixed_overflow_wrap_now_handled_per_glyph()
    {
        // Per Phase 3 Task 10 cycle 3d sub-cycle 2 — mixed-mode
        // overflow-wrap (Normal vs Anywhere across source runs) is
        // now HANDLED via per-source-run plumbing through
        // <see cref="LineBuilder.Wrap"/>'s `overflowWrapPerRun`
        // parameter. Cycle 3b would have thrown
        // NotSupportedException; cycle 3d sub-cycle 2 builds the
        // array + the anywhere fallback gates per-glyph by source-
        // run-index.
        using var resolver = new TestShaperResolver();
        var sNormal = MakeStyle();
        var sAnywhere = ComputedStyle.RentForExclusiveTesting();
        sAnywhere.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere

        var sourceRuns = new List<TextRun>
        {
            new("AAA", sNormal),
            new("BBB", sAnywhere),
        };

        var result = InlineLayouter.LayoutPerRun(sourceRuns, 15, resolver,
            LatnScript, EnLang);
        Assert.NotEmpty(result.Lines);
    }

    [Fact]
    public void LayoutPerRun_mixed_hyphens_now_handled_per_run()
    {
        // Cycle 3d sub-cycle 4 — per-source-run Hyphens via the
        // hyphenation pipeline. Cycle 3b would have thrown
        // NotSupportedException; sub-cycle 4 plumbs per-position
        // Hyphens decisions through soft-hyphen + Liang passes.
        using var resolver = new TestShaperResolver();
        var sManual = MakeStyle(); // default Hyphens.Manual
        var sAuto = ComputedStyle.RentForExclusiveTesting();
        sAuto.Set(PropertyId.Hyphens, ComputedSlot.FromKeyword(2)); // auto

        var sourceRuns = new List<TextRun>
        {
            new("foo", sManual),
            new("bar", sAuto),
        };

        var result = InlineLayouter.LayoutPerRun(sourceRuns, 100, resolver,
            LatnScript, EnLang);
        Assert.NotEmpty(result.Lines);
    }

    [Fact]
    public void LayoutPerRun_singleton_run_works()
    {
        // Single run = trivially uniform; no contention possible.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA", MakeStyleWithNoWrap()) };
        var result = InlineLayouter.LayoutPerRun(sourceRuns, 1000, resolver,
            LatnScript, EnLang);
        Assert.Single(result.Lines);
    }

    // --- Cycle 3b post-PR-41 review hardening tests ------------

    [Fact]
    public void LayoutPerRun_null_resolver_throws_before_policy_scan()
    {
        // Per Phase 3 Task 10 cycle 3b review (User #2 + Copilot #1)
        // — front-loaded validation. Null resolver throws
        // ArgumentNullException, not the mixed-mode NotSupportedException.
        var sourceRuns = new List<TextRun>
        {
            new("AAA", MakeStyle()),
            new("BBB", MakeStyleWithNoWrap()), // mixed
        };
        Assert.Throws<ArgumentNullException>(() =>
            InlineLayouter.LayoutPerRun(sourceRuns, 100, null!, LatnScript, EnLang));
    }

    [Fact]
    public void LayoutPerRun_invalid_inlineSize_throws_before_policy_scan()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("AAA", MakeStyle()),
            new("BBB", MakeStyleWithNoWrap()), // mixed
        };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InlineLayouter.LayoutPerRun(sourceRuns, -1, resolver, LatnScript, EnLang));
    }

    [Fact]
    public void LayoutPerRun_undefined_paragraphDirection_throws()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA", MakeStyle()) };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InlineLayouter.LayoutPerRun(sourceRuns, 100, resolver, LatnScript, EnLang,
                paragraphDirection: (NetPdf.Text.Bidi.ParagraphDirection)99));
    }

    [Fact]
    public void LayoutPerRun_precancelled_token_throws_before_policy_scan()
    {
        // Per User #2 — pre-cancelled token throws
        // OperationCanceledException at entry, before per-run policy
        // scan or mixed-mode check.
        using var resolver = new TestShaperResolver();
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        var sourceRuns = new List<TextRun>
        {
            new("AAA", MakeStyle()),
            new("BBB", MakeStyleWithNoWrap()), // mixed
        };
        Assert.Throws<OperationCanceledException>(() =>
            InlineLayouter.LayoutPerRun(sourceRuns, 100, resolver, LatnScript, EnLang,
                cancellationToken: cts.Token));
    }

    [Fact]
    public void LayoutPerRun_empty_runs_with_mixed_styles_does_NOT_throw()
    {
        // Per User #3 — empty TextRuns contribute no glyphs; their
        // styles should be IGNORED for the policy-equality check.
        // Two empty runs with different styles + one non-empty run
        // resolves to the non-empty run's policy.
        using var resolver = new TestShaperResolver();
        var sNormal = MakeStyle();
        var sNoWrap = MakeStyleWithNoWrap();
        var sourceRuns = new List<TextRun>
        {
            new("", sNormal),
            new("", sNoWrap), // different style but empty — should be ignored
            new("AAA", sNormal),
        };
        // Should not throw; uses sNormal's policy.
        var result = InlineLayouter.LayoutPerRun(sourceRuns, 1000, resolver,
            LatnScript, EnLang);
        Assert.Single(result.Lines);
    }

    [Fact]
    public void LayoutPerRun_all_empty_runs_returns_empty()
    {
        // Per User #3 — all-empty runs (any style mix) is well-
        // defined: returns empty result with default policy.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("", MakeStyle()),
            new("", MakeStyleWithNoWrap()),
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns, 100, resolver,
            LatnScript, EnLang);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public void LayoutPerRun_empty_run_then_nonempty_with_different_white_space_handled()
    {
        // Per cycle 3c — mixed-mode white-space among non-empty
        // runs is now HANDLED (not thrown). Empty runs continue to
        // be ignored for policy comparison purposes.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("", MakeStyleWithNoWrap()), // empty, ignored
            new("AAA", MakeStyle()),         // non-empty Normal
            new("BBB", MakeStyleWithNoWrap()), // non-empty NoWrap → handled per-glyph
        };
        var result = InlineLayouter.LayoutPerRun(sourceRuns, 100, resolver,
            LatnScript, EnLang);
        Assert.NotEmpty(result.Lines);
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
