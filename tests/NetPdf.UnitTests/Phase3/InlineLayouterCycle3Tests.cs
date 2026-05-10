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

/// <summary>Per Phase 3 Task 10 cycle 3 — tests for the
/// <see cref="InlineLayouter"/> overload that reads
/// <see cref="InlineTextPolicy"/> from a containing-block
/// <see cref="ComputedStyle"/>. Closes the cycle-1/2 gap where
/// callers manually passed 4 wrap-policy enums; the block-layouter
/// can now pass the block's own ComputedStyle + the chain auto-
/// resolves the policy bundle.</summary>
public sealed class InlineLayouterCycle3Tests
{
    private const string LatnScript = "Latn";
    private const string EnLang = "en";

    [Fact]
    public void Layout_with_ComputedStyle_null_throws()
    {
        using var resolver = new TestShaperResolver();
        Assert.Throws<ArgumentNullException>(() =>
            InlineLayouter.Layout(
                Array.Empty<TextRun>(),
                100,
                resolver,
                containingBlockStyle: null!,
                LatnScript,
                EnLang));
    }

    [Fact]
    public void Layout_with_default_ComputedStyle_uses_defaults()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA", MakeStyle()) };
        var blockStyle = ComputedStyle.RentForExclusiveTesting();

        var result = InlineLayouter.Layout(sourceRuns, 100, resolver,
            containingBlockStyle: blockStyle,
            scriptIso15924: LatnScript,
            language: EnLang);

        Assert.Single(result);
    }

    [Fact]
    public void Layout_reads_NoWrap_from_ComputedStyle_keyword_id()
    {
        // ComputedStyle with WhiteSpace keyword id 2 (= nowrap) →
        // Layout should suppress soft-wrap (NoWrap mode).
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAA AAA", MakeStyle()) };
        var blockStyle = ComputedStyle.RentForExclusiveTesting();
        blockStyle.Set(PropertyId.WhiteSpace, ComputedSlot.FromKeyword(2)); // nowrap

        var result = InlineLayouter.Layout(sourceRuns, 25, resolver,
            containingBlockStyle: blockStyle,
            scriptIso15924: LatnScript,
            language: EnLang);

        // NoWrap suppresses soft-wrap — should be 1 line not 2.
        Assert.Single(result);
    }

    [Fact]
    public void Layout_reads_OverflowWrap_Anywhere_from_ComputedStyle()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAAA", MakeStyle()) };
        var blockStyle = ComputedStyle.RentForExclusiveTesting();
        blockStyle.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere

        var result = InlineLayouter.Layout(sourceRuns, 15, resolver,
            containingBlockStyle: blockStyle,
            scriptIso15924: LatnScript,
            language: EnLang);

        // Anywhere splits unbreakable run.
        Assert.True(result.Length >= 3);
    }

    [Fact]
    public void Layout_reads_WordBreak_BreakAll_from_ComputedStyle()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAAA", MakeStyle()) };
        var blockStyle = ComputedStyle.RentForExclusiveTesting();
        blockStyle.Set(PropertyId.WordBreak, ComputedSlot.FromKeyword(1)); // break-all

        var result = InlineLayouter.Layout(sourceRuns, 15, resolver,
            containingBlockStyle: blockStyle,
            scriptIso15924: LatnScript,
            language: EnLang);

        Assert.True(result.Length >= 3);
    }

    [Fact]
    public void Layout_reads_Hyphens_Auto_from_ComputedStyle()
    {
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("hyphenation", MakeStyle()) };
        var blockStyle = ComputedStyle.RentForExclusiveTesting();
        blockStyle.Set(PropertyId.Hyphens, ComputedSlot.FromKeyword(2)); // auto

        var result = InlineLayouter.Layout(sourceRuns, 30, resolver,
            containingBlockStyle: blockStyle,
            scriptIso15924: LatnScript,
            language: EnLang);

        // Auto + Liang patterns split "hyphenation".
        Assert.True(result.Length >= 2);
    }

    [Fact]
    public void Layout_resolves_word_break_break_word_alias_via_materializer()
    {
        // Verifies the cross-property fold: word-break: break-word
        // (keyword id 3) → bumps overflow-wrap to Anywhere.
        using var resolver = new TestShaperResolver();
        var sourceRuns = new List<TextRun> { new("AAAAAA", MakeStyle()) };
        var blockStyle = ComputedStyle.RentForExclusiveTesting();
        blockStyle.Set(PropertyId.WordBreak, ComputedSlot.FromKeyword(3)); // break-word

        var result = InlineLayouter.Layout(sourceRuns, 15, resolver,
            containingBlockStyle: blockStyle,
            scriptIso15924: LatnScript,
            language: EnLang);

        // word-break:break-word → folds to overflow-wrap:anywhere
        // → splits unbreakable run.
        Assert.True(result.Length >= 3);
    }

    [Fact]
    public void Layout_with_mixed_run_styles_silently_uniform_pinned()
    {
        // Per Phase 3 Task 10 cycle 3 review (User #4) — pin the
        // documented limit: when source TextRuns have DIFFERENT
        // policies (mixed-mode descendants), this overload applies
        // the containing-block policy uniformly. Per-source-run
        // policy is cycle 3b's responsibility. Output silently
        // reflects the containing-block policy — no exception.
        //
        // Test scenario: containing block has Normal whitespace;
        // one TextRun has its own NoWrap-marked style. Expected
        // (cycle 3 behavior): containing-block Normal applies; the
        // run's own style is IGNORED. Cycle 3b will flip this to
        // honor the per-run NoWrap.
        using var resolver = new TestShaperResolver();

        var containingBlockStyle = ComputedStyle.RentForExclusiveTesting();
        // Containing block: white-space:normal (default keyword id 0).

        var runStyleNormal = MakeStyle();
        var runStyleNoWrap = ComputedStyle.RentForExclusiveTesting();
        runStyleNoWrap.Set(PropertyId.WhiteSpace, ComputedSlot.FromKeyword(2)); // nowrap

        var sourceRuns = new List<TextRun>
        {
            new("AAA AAA", runStyleNormal),
            new(" BBB BBB", runStyleNoWrap), // would prevent wrap if per-run honored
        };

        var result = InlineLayouter.Layout(sourceRuns, 25, resolver,
            containingBlockStyle: containingBlockStyle,
            scriptIso15924: LatnScript,
            language: EnLang);

        // Cycle 3 pinned: containing-block Normal applies — soft-
        // wrap fires at spaces. Result has multiple lines because
        // the run-level NoWrap is ignored.
        // Cycle 3b fix: would honor run-level NoWrap, producing
        // fewer lines.
        Assert.True(result.Length >= 2,
            $"Cycle 3 uniform-policy: containing-block Normal allows wrap; got {result.Length} lines.");
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
