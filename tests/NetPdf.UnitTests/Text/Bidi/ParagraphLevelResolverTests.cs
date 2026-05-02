// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Bidi;
using Xunit;

namespace NetPdf.UnitTests.Text.Bidi;

/// <summary>
/// Tests for UAX #9 P2/P3 paragraph-level resolution. Covers the explicit-direction
/// short-circuits, first-strong-character detection, and the isolate-skipping behavior
/// the algorithm requires.
/// </summary>
public sealed class ParagraphLevelResolverTests
{
    [Fact]
    public void Explicit_LeftToRight_short_circuits_to_level_zero()
    {
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve("hello", ParagraphDirection.LeftToRight));
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve("אבג", ParagraphDirection.LeftToRight)); // Hebrew text but explicit LTR
    }

    [Fact]
    public void Explicit_RightToLeft_short_circuits_to_level_one()
    {
        Assert.Equal((byte)1, ParagraphLevelResolver.Resolve("hello", ParagraphDirection.RightToLeft));
        Assert.Equal((byte)1, ParagraphLevelResolver.Resolve(string.Empty, ParagraphDirection.RightToLeft));
    }

    [Fact]
    public void Auto_with_first_strong_L_returns_level_zero()
    {
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve("Hello World", ParagraphDirection.Auto));
    }

    [Fact]
    public void Auto_with_first_strong_R_returns_level_one()
    {
        // א is Hebrew letter Alef → R.
        Assert.Equal((byte)1, ParagraphLevelResolver.Resolve("אבג", ParagraphDirection.Auto));
    }

    [Fact]
    public void Auto_with_first_strong_AL_returns_level_one()
    {
        // ا is Arabic letter Alef → AL.
        Assert.Equal((byte)1, ParagraphLevelResolver.Resolve("ابة", ParagraphDirection.Auto));
    }

    [Fact]
    public void Auto_skips_neutrals_until_first_strong_character()
    {
        // Spaces, digits, punctuation are not "strong" — Hebrew alef wins.
        Assert.Equal((byte)1, ParagraphLevelResolver.Resolve("   123 אbc", ParagraphDirection.Auto));
    }

    [Fact]
    public void Auto_with_no_strong_character_defaults_to_level_zero()
    {
        // Per P3 — no L/R/AL found, default LTR.
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve("   ", ParagraphDirection.Auto));
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve("123 456", ParagraphDirection.Auto));
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve(string.Empty, ParagraphDirection.Auto));
    }

    [Fact]
    public void Auto_skips_strong_characters_inside_isolate_pair()
    {
        // ⁧ = RLI (isolate); ⁩ = PDI (close). Strong characters inside a balanced
        // isolate pair are NOT considered for paragraph-level resolution per P2.
        // Sequence: RLI א PDI L → first strong outside any isolate is the L.
        const string text = "⁧א⁩M";
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve(text, ParagraphDirection.Auto));
    }

    [Fact]
    public void Auto_treats_unmatched_isolate_initiator_as_skipping_to_end()
    {
        // ⁧ (RLI) with no closing PDI → everything after is "inside" an unmatched
        // isolate and skipped. Since no L/R/AL is seen outside an isolate, P3 → level 0.
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve("⁧אM", ParagraphDirection.Auto));
    }

    [Fact]
    public void Auto_handles_supplementary_plane_codepoint_correctly()
    {
        // U+1F600 (😀) is ON in UCD. The surrogate pair must be read as one codepoint
        // — otherwise the lone high surrogate would be classified separately and the
        // P-rule scan would visit two indices instead of one. With no strong char,
        // P3 default → level 0.
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve("😀", ParagraphDirection.Auto));
    }

    [Fact]
    public void Resolve_throws_on_unknown_direction_value()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ParagraphLevelResolver.Resolve("test", (ParagraphDirection)99));
    }

    // ───── Post-Stage-12.1 hardening: paragraph segmentation + class-data fixes ────

    [Fact]
    public void Auto_stops_at_first_paragraph_separator_outside_isolate()
    {
        // First paragraph "123" has no strong char; LF is class B.
        // P3 default for the first paragraph → 0 (LTR), regardless of what comes after.
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve("123\nאבג", ParagraphDirection.Auto));
    }

    [Fact]
    public void Auto_first_strong_in_first_paragraph_wins_over_later_paragraphs()
    {
        // First paragraph has Hebrew alef → R → level 1. The L in the second paragraph
        // must NOT influence the result.
        Assert.Equal((byte)1, ParagraphLevelResolver.Resolve("אבג\nHello", ParagraphDirection.Auto));
    }

    [Fact]
    public void Auto_first_paragraph_with_only_neutrals_returns_LTR_per_P3()
    {
        // "  " has no strong characters; B stops the scan; P3 default → 0. Pre-fix this
        // would have continued into "אבג" and returned 1.
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve("  \nאבג", ParagraphDirection.Auto));
    }

    [Fact]
    public void Emoji_followed_by_RTL_letter_resolves_to_RTL()
    {
        // Reviewer's recommended case: 😀 (U+1F600, ON) is not strong, so the next
        // strong char wins. א is Hebrew alef (R) → level 1.
        // Pre-fix this returned 0 because emoji defaulted to L.
        Assert.Equal((byte)1, ParagraphLevelResolver.Resolve("😀אבג", ParagraphDirection.Auto));
    }

    [Fact]
    public void Arabic_number_sign_followed_by_LTR_letter_resolves_to_LTR()
    {
        // Reviewer's recommended case: U+0600 ARABIC NUMBER SIGN is AN (not strong),
        // so 'A' (L) wins → level 0. Pre-fix this returned 1 because U+0600 was
        // misclassified as AL.
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve("؀A", ParagraphDirection.Auto));
    }

    [Fact]
    public void Arabic_Extended_A_letter_at_paragraph_start_resolves_to_RTL()
    {
        // U+08A0 is AL — pre-fix it defaulted to L, breaking auto-detection for any
        // paragraph that opens with an Arabic Extended-A character.
        Assert.Equal((byte)1, ParagraphLevelResolver.Resolve("ࢠbc", ParagraphDirection.Auto));
    }

    [Fact]
    public void Hebrew_presentation_form_at_paragraph_start_resolves_to_RTL()
    {
        // U+FB1D is R (Hebrew Presentation Form) — pre-fix it defaulted to L.
        Assert.Equal((byte)1, ParagraphLevelResolver.Resolve("יִxyz", ParagraphDirection.Auto));
    }

    [Fact]
    public void Arabic_presentation_form_at_paragraph_start_resolves_to_RTL()
    {
        // U+FE70 is AL (Arabic Presentation Forms-B) — pre-fix it defaulted to L.
        Assert.Equal((byte)1, ParagraphLevelResolver.Resolve("ﹰxyz", ParagraphDirection.Auto));
    }
}
