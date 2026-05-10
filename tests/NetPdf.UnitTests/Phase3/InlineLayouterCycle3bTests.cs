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
        Assert.Empty(result);
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
        Assert.Single(result);
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
        Assert.Single(result);
    }

    [Fact]
    public void LayoutPerRun_mixed_white_space_throws_NotSupported()
    {
        // First run: default Normal; second run: NoWrap (keyword id 2).
        using var resolver = new TestShaperResolver();
        var sNormal = MakeStyle();
        var sNoWrap = MakeStyleWithNoWrap();
        var sourceRuns = new List<TextRun>
        {
            new("AAA", sNormal),
            new(" BBB", sNoWrap),
        };
        var ex = Assert.Throws<NotSupportedException>(() =>
            InlineLayouter.LayoutPerRun(sourceRuns, 100, resolver,
                LatnScript, EnLang));
        Assert.Contains("different InlineTextPolicy", ex.Message);
        Assert.Contains("cycle 3c", ex.Message);
    }

    [Fact]
    public void LayoutPerRun_mixed_overflow_wrap_throws_NotSupported()
    {
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
            InlineLayouter.LayoutPerRun(sourceRuns, 15, resolver,
                LatnScript, EnLang));
    }

    [Fact]
    public void LayoutPerRun_mixed_hyphens_throws_NotSupported()
    {
        using var resolver = new TestShaperResolver();
        var sManual = MakeStyle(); // default Hyphens.Manual
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
    public void LayoutPerRun_singleton_run_works()
    {
        // Single run = trivially uniform; no contention possible.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA", MakeStyleWithNoWrap()) };
        var result = InlineLayouter.LayoutPerRun(sourceRuns, 1000, resolver,
            LatnScript, EnLang);
        Assert.Single(result);
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
        Assert.Single(result);
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
        Assert.Empty(result);
    }

    [Fact]
    public void LayoutPerRun_empty_run_then_nonempty_with_different_style_throws()
    {
        // Mixed-mode is detected ONLY among non-empty runs. If two
        // non-empty runs have different policies, throw.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun>
        {
            new("", MakeStyleWithNoWrap()), // empty, ignored
            new("AAA", MakeStyle()),         // non-empty Normal
            new("BBB", MakeStyleWithNoWrap()), // non-empty NoWrap → mixed
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
