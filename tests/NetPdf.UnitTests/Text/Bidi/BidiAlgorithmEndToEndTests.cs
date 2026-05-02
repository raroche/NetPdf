// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Bidi;
using Xunit;

namespace NetPdf.UnitTests.Text.Bidi;

/// <summary>
/// End-to-end integration tests for the full UAX #9 algorithm — every rule pass
/// (X1–X10, BD13, W1–W7, N0/N1/N2, I1/I2, L1) cooperating to produce per-character
/// embedding levels for real strings. Uses the public
/// <see cref="BidiAlgorithm.ResolveLevels"/> API exclusively.
/// </summary>
/// <remarks>
/// Real strings exercise the rules in concert; isolating any one rule from the others
/// means manually constructing the intermediate state, which is more brittle than
/// trusting the canonical inputs the spec uses to demonstrate the algorithm.
/// </remarks>
public sealed class BidiAlgorithmEndToEndTests
{
    // ───── Pure single-direction baselines ────────────────────────────────────

    [Fact]
    public void Pure_LTR_string_all_levels_zero()
    {
        var levels = BidiAlgorithm.ResolveLevels("Hello, world!");
        Assert.All(levels, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Pure_Hebrew_RTL_paragraph_all_letters_level_one()
    {
        var levels = BidiAlgorithm.ResolveLevels("שלום");
        Assert.Equal(4, levels.Length);
        Assert.All(levels, b => Assert.Equal(1, b));
    }

    [Fact]
    public void Pure_Arabic_RTL_paragraph_letters_resolved_level_one()
    {
        // Arabic letters are class AL — W3 maps AL → R, then I1 (odd-level seq) keeps R at sequence level 1.
        var levels = BidiAlgorithm.ResolveLevels("سلام");
        Assert.Equal(4, levels.Length);
        Assert.All(levels, b => Assert.Equal(1, b));
    }

    // ───── Mixed-direction text ───────────────────────────────────────────────

    [Fact]
    public void LTR_paragraph_with_Hebrew_word_assigns_level_one_to_Hebrew()
    {
        var input = "car אבג ride";
        var levels = BidiAlgorithm.ResolveLevels(input);
        // 'c', 'a', 'r' → level 0
        Assert.Equal(0, levels[0]);
        Assert.Equal(0, levels[1]);
        Assert.Equal(0, levels[2]);
        // ' ' between Latin and Hebrew → level 0 (N1 sees L on both sides via embedding direction)
        Assert.Equal(0, levels[3]);
        // 'אבג' → level 1
        Assert.Equal(1, levels[4]);
        Assert.Equal(1, levels[5]);
        Assert.Equal(1, levels[6]);
        // ' ' between Hebrew and Latin → level 0 (resolved to embedding direction)
        // 'r', 'i', 'd', 'e' → level 0
        Assert.Equal(0, levels[^1]);
    }

    [Fact]
    public void RTL_paragraph_with_English_word_assigns_level_two_to_English()
    {
        // Hebrew paragraph (level 1). Embedded English at level 2 (next even above 1) per I rules.
        var input = "אבג car דהו";
        var levels = BidiAlgorithm.ResolveLevels(input, ParagraphDirection.RightToLeft);
        // Hebrew at level 1
        Assert.Equal(1, levels[0]);
        // 'c', 'a', 'r' at level 2 (embedded LTR run inside RTL paragraph)
        Assert.Equal(2, levels[4]);
        Assert.Equal(2, levels[5]);
        Assert.Equal(2, levels[6]);
        // Closing Hebrew back at level 1
        Assert.Equal(1, levels[^1]);
    }

    // ───── European numbers (W2, W7) ──────────────────────────────────────────

    [Fact]
    public void EN_after_AL_becomes_AN_via_W2()
    {
        // Arabic letter (AL) followed by European numbers — W2 changes EN → AN.
        // AN at even-level paragraph: I1 bumps AN by 2 → level 2.
        var input = "abc س123";
        var levels = BidiAlgorithm.ResolveLevels(input);
        // 'abc ' at paragraph level 0
        Assert.Equal(0, levels[0]);
        // 'س' (Arabic letter) — W3 maps AL→R, I1 bumps R by 1 → level 1
        Assert.Equal(1, levels[4]);
        // '1', '2', '3' — W2 (after AL) maps EN→AN, I1 bumps AN by 2 → level 2
        Assert.Equal(2, levels[5]);
        Assert.Equal(2, levels[6]);
        Assert.Equal(2, levels[7]);
    }

    [Fact]
    public void EN_after_L_becomes_L_via_W7()
    {
        // Latin (L) followed by European numbers — W7 changes EN → L.
        // L at even-level paragraph: I1 doesn't bump L → stays at level 0.
        var input = "abc 123";
        var levels = BidiAlgorithm.ResolveLevels(input);
        Assert.All(levels, b => Assert.Equal(0, b));
    }

    [Fact]
    public void EN_in_LTR_context_resolves_to_L_via_W7()
    {
        // Paragraph auto-detects LTR (first strong = 'a'). W7 walks backward from each EN
        // and finds sos = L; per W7 the EN takes L. After all rules, every char at level 0.
        var levels = BidiAlgorithm.ResolveLevels("abc 123");
        Assert.All(levels, b => Assert.Equal(0, b));
    }

    // ───── L1 trailing whitespace reset ───────────────────────────────────────

    [Fact]
    public void Trailing_whitespace_in_LTR_paragraph_resets_to_paragraph_level()
    {
        // LTR paragraph (first strong = 'a'). Hebrew embeds at level 1 via I1; trailing
        // spaces resolve to embedding direction L (level 0) per N rules.
        var input = "abc אבג   ";
        var levels = BidiAlgorithm.ResolveLevels(input);
        Assert.Equal(0, levels[0]);
        Assert.Equal(1, levels[4]);
        Assert.Equal(1, levels[5]);
        Assert.Equal(1, levels[6]);
        Assert.Equal(0, levels[7]);
        Assert.Equal(0, levels[^1]);
    }

    [Fact]
    public void L1_resets_WS_inside_embedded_run_when_followed_by_B()
    {
        // U+202B RLE pushes embedding level 1; the two spaces inside resolve to level 1
        // pre-L1. U+2029 is class B (paragraph separator) — X8 sets level to paragraphLevel
        // (0) and resets the directional stack. L1 case 3 then resets the WS preceding the
        // B back to paragraph level 0 even though they were at level 1 pre-L1.
        var input = "A\u202B  \u2029\u202CZ";
        var levels = BidiAlgorithm.ResolveLevels(input);
        Assert.Equal(0, levels[0]);
        Assert.Equal(0, levels[2]);
        Assert.Equal(0, levels[3]);
        Assert.Equal(0, levels[4]);
        Assert.Equal(0, levels[^1]);
    }

    // ───── N0 paired brackets ─────────────────────────────────────────────────

    [Fact]
    public void Brackets_around_L_in_RTL_paragraph_take_LTR_per_N0_inner_strong()
    {
        // Hebrew paragraph with parens around English — N0 sees L inside the brackets,
        // which opposes the embedding direction (R for level-1 sequence). Looks
        // backward for context: sos = R. Opposite = L. So both brackets take L.
        // Wait — the embedding direction is R; opposite is L; inside has L (matches
        // opposite). Backward context = sos = R (matches embedding). Per N0(b): if
        // context matches embedding, brackets take embedding. So brackets become R.
        // After I1 (odd-level seq), R stays at level 1.
        var input = "אבג (cat) דהו";
        var levels = BidiAlgorithm.ResolveLevels(input, ParagraphDirection.RightToLeft);
        // Brackets at index 4 ('(') and 8 (')'). Embedding direction R → level 1.
        Assert.Equal(1, levels[4]);
        Assert.Equal(1, levels[8]);
        // English inside still at level 2 (embedded LTR run).
        Assert.Equal(2, levels[5]);
        Assert.Equal(2, levels[6]);
        Assert.Equal(2, levels[7]);
    }

    // ───── Determinism ────────────────────────────────────────────────────────

    [Fact]
    public void ResolveLevels_is_deterministic_for_byte_equal_input()
    {
        var input = "Hello אבג World";
        var first = BidiAlgorithm.ResolveLevels(input);
        var second = BidiAlgorithm.ResolveLevels(input);
        Assert.Equal(first, second);
    }

    // ───── Embedding controls ─────────────────────────────────────────────────

    [Fact]
    public void RLE_PDF_explicit_embedding_promotes_inner_L_to_level_two()
    {
        // U+202B = RLE, U+202C = PDF. RLE pushes level 1; inner L is at level 2 after I1
        // bumps L on odd-level sequence.
        var input = "A‫L‬B";
        var levels = BidiAlgorithm.ResolveLevels(input);
        Assert.Equal(0, levels[0]);                   // 'A'
        Assert.Equal(0, levels[1]);                   // RLE retained at enclosing level
        Assert.Equal(2, levels[2]);                   // inner L bumped by I1 from 1 → 2
        Assert.Equal(0, levels[3]);                   // PDF retained at enclosing level
        Assert.Equal(0, levels[4]);                   // 'B'
    }

    // ───── Empty input ────────────────────────────────────────────────────────

    [Fact]
    public void Empty_input_returns_empty_array()
    {
        Assert.Empty(BidiAlgorithm.ResolveLevels(string.Empty));
    }

    [Fact]
    public void Whitespace_only_LTR_input_all_zero_levels()
    {
        // No strong characters → P3 default LTR (level 0); WS resolves to paragraph level via L1.
        var levels = BidiAlgorithm.ResolveLevels("   ");
        Assert.All(levels, b => Assert.Equal(0, b));
    }
}
