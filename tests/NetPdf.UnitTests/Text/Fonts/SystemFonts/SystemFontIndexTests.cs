// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.SystemFonts;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.SystemFonts;

/// <summary>
/// CSS-style face-matching tests for <see cref="SystemFontIndex"/>. Uses synthetic
/// <see cref="SystemFontEntry"/> records so the matching algorithm can be exercised
/// independently of any host-platform font set.
/// </summary>
public sealed class SystemFontIndexTests
{
    private static SystemFontEntry Entry(string family, int weight, bool italic, string subfamily = "Regular", int stretch = 5) =>
        new()
        {
            FilePath = $"/fake/{family}-{subfamily}-w{weight}-s{stretch}.ttf",
            FaceIndex = 0,
            FamilyName = family,
            SubfamilyName = subfamily,
            PostScriptName = $"{family}-{subfamily}".Replace(" ", string.Empty),
            WeightCss = weight,
            StretchCss = stretch,
            IsItalic = italic,
        };

    [Fact]
    public void Build_groups_entries_by_family_name_case_insensitively()
    {
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", 400, italic: false),
            Entry("roboto", 700, italic: false),       // same family, different case
            Entry("Open Sans", 400, italic: false),
        ]);
        Assert.Equal(2, idx.FamilyCount);
        Assert.Equal(3, idx.Count);
        Assert.True(idx.HasFamily("Roboto"));
        Assert.True(idx.HasFamily("ROBOTO"));
        Assert.True(idx.HasFamily("Open Sans"));
        Assert.False(idx.HasFamily("Helvetica"));
    }

    [Fact]
    public void FindBest_exact_weight_and_italic_match_wins()
    {
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", 400, italic: false),
            Entry("Roboto", 400, italic: true),
            Entry("Roboto", 700, italic: false),
            Entry("Roboto", 700, italic: true),
        ]);
        var best = idx.FindBest("Roboto", weightCss: 700, italic: true);
        Assert.NotNull(best);
        Assert.Equal(700, best.Value.WeightCss);
        Assert.True(best.Value.IsItalic);
    }

    [Fact]
    public void FindBest_italic_mismatch_is_dominant_penalty()
    {
        // Request: weight=400, italic=true. Available: italic=false@400 vs italic=true@900.
        // The italic=true face should win even though its weight is much further off.
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", 400, italic: false),
            Entry("Roboto", 900, italic: true),
        ]);
        var best = idx.FindBest("Roboto", weightCss: 400, italic: true);
        Assert.NotNull(best);
        Assert.True(best.Value.IsItalic);
        Assert.Equal(900, best.Value.WeightCss);
    }

    [Fact]
    public void FindBest_light_regime_picks_lighter_or_equal_first_per_spec()
    {
        // CSS Fonts 4 §5.2.4 light regime (request < 400): tier 1 = lighter-or-equal
        // descending, tier 2 = heavier ascending. Request 300 with candidates 200 and 400:
        // tier 1 contains 200 → 200 wins regardless of distance to 400.
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", 200, italic: false),
            Entry("Roboto", 400, italic: false),
        ]);
        var best = idx.FindBest("Roboto", weightCss: 300, italic: false);
        Assert.NotNull(best);
        Assert.Equal(200, best.Value.WeightCss);
    }

    [Fact]
    public void FindBest_heavy_regime_picks_heavier_or_equal_first_per_spec()
    {
        // CSS Fonts 4 §5.2.4 heavy regime (request > 500): tier 1 = heavier-or-equal
        // ascending, tier 2 = lighter descending. Request 700 with candidates 500 and 800:
        // tier 1 contains 800 → 800 wins.
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", 500, italic: false),
            Entry("Roboto", 800, italic: false),
        ]);
        var best = idx.FindBest("Roboto", weightCss: 700, italic: false);
        Assert.NotNull(best);
        Assert.Equal(800, best.Value.WeightCss);
    }

    [Fact]
    public void FindBest_returns_null_for_unknown_family()
    {
        var idx = SystemFontIndex.BuildFromEntries([Entry("Roboto", 400, italic: false)]);
        Assert.Null(idx.FindBest("Helvetica", weightCss: 400, italic: false));
    }

    [Fact]
    public void Build_skips_entries_with_empty_family_name()
    {
        var idx = SystemFontIndex.BuildFromEntries([
            Entry(string.Empty, 400, italic: false),
            Entry("Roboto", 400, italic: false),
        ]);
        Assert.Equal(1, idx.Count);
        Assert.Equal(1, idx.FamilyCount);
    }

    [Fact]
    public void Build_throws_for_null_enumerator()
    {
        Assert.Throws<ArgumentNullException>(() => SystemFontIndex.Build(null!));
    }

    [Fact]
    public void BuildFromEntries_throws_for_null_input()
    {
        Assert.Throws<ArgumentNullException>(() => SystemFontIndex.BuildFromEntries(null!));
    }

    // ───── CSS Fonts 4 §5.2 stretch axis (review #1, #2, #3) ──────────────────

    [Fact]
    public void FindBest_exact_stretch_match_wins_over_weight_nearer_wrong_stretch()
    {
        // Request: weight=400, italic=false, stretch=3 (Condensed). Available:
        //   - weight=900 stretch=3 (perfect stretch, far weight)
        //   - weight=400 stretch=5 (perfect weight, off stretch)
        // Per CSS Fonts 4 §5.2 stretch is processed FIRST — the stretch=3 candidate must win
        // even though its weight is much further off.
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", weight: 900, italic: false, stretch: 3),
            Entry("Roboto", weight: 400, italic: false, stretch: 5),
        ]);
        var best = idx.FindBest("Roboto", weightCss: 400, italic: false, stretchCss: 3);
        Assert.NotNull(best);
        Assert.Equal(3, best.Value.StretchCss);
        Assert.Equal(900, best.Value.WeightCss);
    }

    [Fact]
    public void FindBest_stretch_dominates_italic_too()
    {
        // Request: weight=400, italic=true, stretch=7 (Expanded). Available:
        //   - italic=true stretch=5 (italic correct, stretch off)
        //   - italic=false stretch=7 (perfect stretch, italic wrong)
        // Stretch is the dominant axis: the stretch=7 candidate wins despite italic mismatch.
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", weight: 400, italic: true, stretch: 5),
            Entry("Roboto", weight: 400, italic: false, stretch: 7),
        ]);
        var best = idx.FindBest("Roboto", weightCss: 400, italic: true, stretchCss: 7);
        Assert.NotNull(best);
        Assert.Equal(7, best.Value.StretchCss);
        Assert.False(best.Value.IsItalic);
    }

    [Fact]
    public void FindBest_when_request_le_5_prefers_narrower_on_stretch_ties()
    {
        // Request stretch=4 (Semi-Condensed). Available stretch=3 and stretch=5 (both delta=1).
        // Per CSS Fonts 4 §5.2: when request ≤ 5, narrower (lower) wins on ties.
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", 400, italic: false, stretch: 3),
            Entry("Roboto", 400, italic: false, stretch: 5),
        ]);
        var best = idx.FindBest("Roboto", weightCss: 400, italic: false, stretchCss: 4);
        Assert.NotNull(best);
        Assert.Equal(3, best.Value.StretchCss);
    }

    [Fact]
    public void FindBest_when_request_gt_5_prefers_wider_on_stretch_ties()
    {
        // Request stretch=6 (Semi-Expanded). Available stretch=5 and stretch=7 (both delta=1).
        // Per CSS Fonts 4 §5.2: when request > 5, wider (higher) wins on ties.
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", 400, italic: false, stretch: 5),
            Entry("Roboto", 400, italic: false, stretch: 7),
        ]);
        var best = idx.FindBest("Roboto", weightCss: 400, italic: false, stretchCss: 6);
        Assert.NotNull(best);
        Assert.Equal(7, best.Value.StretchCss);
    }

    [Fact]
    public void FindBest_within_same_stretch_falls_back_to_style_then_weight()
    {
        // All candidates at stretch=5 — should fall through to the style/weight axes
        // exactly as in the no-stretch tests above.
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", 400, italic: false, stretch: 5),
            Entry("Roboto", 700, italic: false, stretch: 5),
            Entry("Roboto", 400, italic: true, stretch: 5),
            Entry("Roboto", 700, italic: true, stretch: 5),
        ]);
        var best = idx.FindBest("Roboto", weightCss: 700, italic: true, stretchCss: 5);
        Assert.NotNull(best);
        Assert.Equal(700, best.Value.WeightCss);
        Assert.True(best.Value.IsItalic);
    }

    [Fact]
    public void FindBest_default_stretch_argument_is_5_normal()
    {
        // Backward-compat: existing callers that don't pass stretchCss should get the same
        // result as if they passed 5 (normal width).
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", 400, italic: false, stretch: 3),
            Entry("Roboto", 400, italic: false, stretch: 5),
        ]);
        var defaulted = idx.FindBest("Roboto", weightCss: 400, italic: false);
        var explicitNormal = idx.FindBest("Roboto", weightCss: 400, italic: false, stretchCss: 5);
        Assert.Equal(explicitNormal!.Value.StretchCss, defaulted!.Value.StretchCss);
    }

    // ───── Spec-accurate non-tie ordering (review follow-up #1, #2) ───────────
    // CSS Fonts 4 §5.2 defines ordered tier searches, NOT distance functions. Below are
    // the reviewer's specific non-tie examples — each exercises a case where naive
    // distance scoring would pick a different candidate than the spec's tiered ordering.

    [Fact]
    public void Stretch_request_4_with_candidates_1_and_5_picks_1()
    {
        // Request 4 ≤ 5 → narrower-or-equal first. Candidate 1 is in tier 1 (narrower),
        // candidate 5 is in tier 2 (wider). Tier 1 has a hit → 1 wins, even though 5 is
        // numerically closer to 4. Naive |distance| scoring would pick 5; ordered search
        // correctly picks 1 per spec §5.2.3.
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", 400, italic: false, stretch: 1),
            Entry("Roboto", 400, italic: false, stretch: 5),
        ]);
        var best = idx.FindBest("Roboto", weightCss: 400, italic: false, stretchCss: 4);
        Assert.NotNull(best);
        Assert.Equal(1, best.Value.StretchCss);
    }

    [Fact]
    public void Stretch_request_7_with_candidates_6_and_9_picks_9()
    {
        // Request 7 > 5 → wider-or-equal first. Candidate 9 is in tier 1 (wider),
        // candidate 6 is in tier 2 (narrower). Tier 1 has a hit → 9 wins, even though 6
        // is numerically closer.
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", 400, italic: false, stretch: 6),
            Entry("Roboto", 400, italic: false, stretch: 9),
        ]);
        var best = idx.FindBest("Roboto", weightCss: 400, italic: false, stretchCss: 7);
        Assert.NotNull(best);
        Assert.Equal(9, best.Value.StretchCss);
    }

    [Fact]
    public void Weight_request_401_with_candidates_399_and_500_picks_500()
    {
        // Request 401 is in normal regime [400, 500]. Tier 1 = [401, 500] ascending →
        // candidate 500 (delta +99). Tier 2 = below 401 → candidate 399. Tier 1 has a
        // hit → 500 wins. Naive distance scoring would pick 399 (delta only -2 vs +99).
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", weight: 399, italic: false),
            Entry("Roboto", weight: 500, italic: false),
        ]);
        var best = idx.FindBest("Roboto", weightCss: 401, italic: false);
        Assert.NotNull(best);
        Assert.Equal(500, best.Value.WeightCss);
    }

    [Fact]
    public void Weight_request_350_with_candidates_300_and_600_picks_300_per_spec()
    {
        // Request 350 < 400 → light regime. Tier 1 = lighter-or-equal descending →
        // candidate 300. Tier 2 = heavier → candidate 600. Tier 1 has a hit → 300 wins.
        // Naive distance scoring already picks 300 here, but this test pins the regime
        // boundary for completeness so a future "tilt-only" reversion is caught.
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", weight: 300, italic: false),
            Entry("Roboto", weight: 600, italic: false),
        ]);
        var best = idx.FindBest("Roboto", weightCss: 350, italic: false);
        Assert.NotNull(best);
        Assert.Equal(300, best.Value.WeightCss);
    }

    [Fact]
    public void Weight_request_450_normal_regime_with_only_candidates_lighter_or_above_500()
    {
        // Request 450 in normal regime [400, 500]. Tier 1 = [450, 500] empty (no
        // candidate). Tier 2 = below 450 → candidate 399. Tier 3 = above 500 → candidate
        // 700. Tier 2 wins → 399, even though 700 has the same |delta| (250).
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", weight: 399, italic: false),
            Entry("Roboto", weight: 700, italic: false),
        ]);
        var best = idx.FindBest("Roboto", weightCss: 450, italic: false);
        Assert.NotNull(best);
        Assert.Equal(399, best.Value.WeightCss);
    }

    [Fact]
    public void Stretch_request_4_falls_through_to_wider_when_no_narrower_available()
    {
        // Request 4. Only candidates wider (6 and 9). Tier 1 (narrower-or-equal) empty
        // → tier 2 (wider) ascending → 6 wins (closest wider).
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", 400, italic: false, stretch: 6),
            Entry("Roboto", 400, italic: false, stretch: 9),
        ]);
        var best = idx.FindBest("Roboto", weightCss: 400, italic: false, stretchCss: 4);
        Assert.NotNull(best);
        Assert.Equal(6, best.Value.StretchCss);
    }

    [Fact]
    public void Stretch_request_7_falls_through_to_narrower_when_no_wider_available()
    {
        // Request 7. Only candidates narrower (3 and 5). Tier 1 (wider-or-equal) empty
        // → tier 2 (narrower) descending → 5 wins (closest narrower).
        var idx = SystemFontIndex.BuildFromEntries([
            Entry("Roboto", 400, italic: false, stretch: 3),
            Entry("Roboto", 400, italic: false, stretch: 5),
        ]);
        var best = idx.FindBest("Roboto", weightCss: 400, italic: false, stretchCss: 7);
        Assert.NotNull(best);
        Assert.Equal(5, best.Value.StretchCss);
    }
}
