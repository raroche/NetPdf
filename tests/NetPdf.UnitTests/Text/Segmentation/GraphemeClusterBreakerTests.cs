// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Segmentation;
using Xunit;

namespace NetPdf.UnitTests.Text.Segmentation;

/// <summary>
/// Granular per-rule and API-level regression tests for the UAX #29 grapheme cluster
/// boundary engine. Complements <see cref="GraphemeBreakUcdConformanceTests"/> by
/// pinning down individual rule behaviors so a regression in one rule shows up as a
/// focused test failure rather than only as an aggregate pass-rate change.
/// </summary>
public sealed class GraphemeClusterBreakerTests
{
    // ───── API shape ─────────────────────────────────────────────────────────

    [Fact]
    public void Empty_input_returns_single_zero_boundary()
    {
        var b = GraphemeClusterBreaker.FindBoundaries(string.Empty);
        Assert.Single(b);
        Assert.Equal(0, b[0]);
    }

    [Fact]
    public void First_boundary_is_always_zero_per_GB1()
    {
        var b = GraphemeClusterBreaker.FindBoundaries("abc");
        Assert.Equal(0, b[0]);
    }

    [Fact]
    public void Last_boundary_is_always_text_length_per_GB2()
    {
        var input = "abc";
        var b = GraphemeClusterBreaker.FindBoundaries(input);
        Assert.Equal(input.Length, b[^1]);
    }

    [Fact]
    public void Pure_ASCII_each_codepoint_is_own_grapheme_cluster()
    {
        // "abc" → 4 boundaries: [0, 1, 2, 3] meaning 3 graphemes.
        var b = GraphemeClusterBreaker.FindBoundaries("abc");
        Assert.Equal([0, 1, 2, 3], b);
    }

    // ───── GB3: CR × LF ───────────────────────────────────────────────────────

    [Fact]
    public void GB3_CRLF_is_a_single_grapheme()
    {
        var b = GraphemeClusterBreaker.FindBoundaries("a\r\nb");
        // boundaries: [0, 1, 3, 4]. The CR-LF pair occupies positions 1-2 as one cluster.
        Assert.Equal([0, 1, 3, 4], b);
    }

    // ───── GB4 / GB5: Control / CR / LF always break ──────────────────────────

    [Fact]
    public void GB4_break_after_Control()
    {
        // U+0007 BELL (Control). After it: break.
        var b = GraphemeClusterBreaker.FindBoundaries("ab");
        Assert.Equal([0, 1, 2, 3], b);
    }

    [Fact]
    public void GB5_break_before_LF()
    {
        // 'a' ÷ LF — break before LF.
        var b = GraphemeClusterBreaker.FindBoundaries("a\nb");
        Assert.Equal([0, 1, 2, 3], b);
    }

    // ───── GB6 / GB7 / GB8: Hangul syllable composition ───────────────────────

    [Fact]
    public void GB6_Hangul_L_followed_by_V_or_LV_or_LVT_no_break()
    {
        // U+1100 (L) + U+1161 (V) — Hangul "GA" syllable, single grapheme.
        var b = GraphemeClusterBreaker.FindBoundaries("가");
        Assert.Equal([0, 2], b);
    }

    [Fact]
    public void GB8_Hangul_LVT_followed_by_T_no_break()
    {
        // U+AC00 (LV) + U+11A8 (T) — single grapheme.
        var b = GraphemeClusterBreaker.FindBoundaries("각");
        Assert.Equal([0, 2], b);
    }

    // ───── GB9: Extend / ZWJ attaches to base ─────────────────────────────────

    [Fact]
    public void GB9_combining_mark_attaches_to_preceding_base()
    {
        // 'a' + U+0301 COMBINING ACUTE ACCENT — single grapheme "á".
        var b = GraphemeClusterBreaker.FindBoundaries("áb");
        Assert.Equal([0, 2, 3], b);
    }

    [Fact]
    public void GB9a_SpacingMark_attaches_to_preceding_base()
    {
        // U+0915 (DEVANAGARI LETTER KA, Other) + U+093E (DEVANAGARI VOWEL SIGN AA, SpacingMark).
        var b = GraphemeClusterBreaker.FindBoundaries("का");
        Assert.Equal([0, 2], b);
    }

    [Fact]
    public void GB9b_Prepend_attaches_to_following_base()
    {
        // U+0600 ARABIC NUMBER SIGN (Prepend) + 'a'.
        var b = GraphemeClusterBreaker.FindBoundaries("؀a");
        Assert.Equal([0, 2], b);
    }

    // ───── GB11: Extended_Pictographic ZWJ sequences ──────────────────────────

    [Fact]
    public void GB11_emoji_ZWJ_emoji_is_single_grapheme()
    {
        // 👨 (U+1F468 Extended_Pictographic) + U+200D ZWJ + 👩 (U+1F469).
        var input = "\U0001F468‍\U0001F469";
        var b = GraphemeClusterBreaker.FindBoundaries(input);
        // Whole sequence is one grapheme: boundaries [0, input.Length].
        Assert.Equal([0, input.Length], b);
    }

    // ───── GB12 / GB13: regional indicator pairs ──────────────────────────────

    [Fact]
    public void GB12_RI_pair_at_sot_is_single_flag_grapheme()
    {
        // 🇺🇸 — U.S. flag = U+1F1FA + U+1F1F8.
        var input = "\U0001F1FA\U0001F1F8";
        var b = GraphemeClusterBreaker.FindBoundaries(input);
        Assert.Equal([0, input.Length], b);
    }

    [Fact]
    public void GB13_third_RI_starts_new_grapheme()
    {
        // 🇺🇸🇯🇵 — two flags. Boundary between them.
        var input = "\U0001F1FA\U0001F1F8\U0001F1EF\U0001F1F5";
        var b = GraphemeClusterBreaker.FindBoundaries(input);
        // Two graphemes: [0, 4, 8].
        Assert.Equal([0, 4, 8], b);
    }

    // ───── GB9c: Indic conjunct break ─────────────────────────────────────────

    [Fact]
    public void GB9c_Indic_conjunct_with_virama_is_single_grapheme()
    {
        // Devanagari KA (U+0915) + Virama (U+094D) + KA (U+0915) — Indic conjunct.
        // Per GB9c: Consonant Linker Consonant is one grapheme.
        var input = "क्क";
        var b = GraphemeClusterBreaker.FindBoundaries(input);
        Assert.Equal([0, input.Length], b);
    }

    // ───── Surrogate pairs ────────────────────────────────────────────────────

    [Fact]
    public void Supplementary_plane_codepoint_is_one_grapheme_spanning_two_code_units()
    {
        // U+1F600 GRINNING FACE — surrogate pair → 1 grapheme.
        var b = GraphemeClusterBreaker.FindBoundaries("a\U0001F600b");
        Assert.Equal([0, 1, 3, 4], b);
    }

    // ───── Determinism ────────────────────────────────────────────────────────

    [Fact]
    public void FindBoundaries_is_deterministic_for_byte_equal_input()
    {
        var input = "Hello, 世界! 🇺🇸 áb";
        var first = GraphemeClusterBreaker.FindBoundaries(input);
        var second = GraphemeClusterBreaker.FindBoundaries(input);
        Assert.Equal(first, second);
    }
}
